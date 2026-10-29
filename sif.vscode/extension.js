const fs = require('fs');
const path = require('path');
const vscode = require('vscode');

let terminal;
let terminalStarted = false;
let statusItem;
let lastEditorPayload;
let debugConsoleFile;

const MAX_DEBUG_CONSOLE_BYTES = 1024 * 1024;

function activate(context) {
  // Use workspace-scoped storage so each window gets its own context file;
  // globalStorageUri is shared across windows and lets them overwrite each other.
  const storageDir = (context.storageUri ?? context.globalStorageUri).fsPath;
  const fileName = context.storageUri
    ? 'editor-context.json'
    : `editor-context-${vscode.env.sessionId}.json`;
  const contextFile = path.join(storageDir, fileName);
  debugConsoleFile = path.join(storageDir, context.storageUri
    ? 'debug-console.log'
    : `debug-console-${vscode.env.sessionId}.log`);
  fs.mkdirSync(storageDir, { recursive: true });
  context.environmentVariableCollection.replace('SIF_VSCODE_CONTEXT_FILE', contextFile);
  registerDebugConsoleCapture(context);

  const update = () => writeEditorContext(contextFile);
  context.subscriptions.push(
    vscode.commands.registerCommand('sif.startChat', () => startChat(contextFile)),
    vscode.commands.registerCommand('sif.updateContext', async () => {
      await update();
      vscode.window.showInformationMessage('sif editor context updated.');
    }),
    vscode.commands.registerCommand('sif.showContextFile', async () => {
      await update();
      const doc = await vscode.workspace.openTextDocument(contextFile);
      await vscode.window.showTextDocument(doc, { preview: true });
    }),
    vscode.window.onDidChangeActiveTextEditor(update),
    vscode.window.onDidChangeTextEditorSelection(update),
    vscode.workspace.onDidSaveTextDocument(update)
  );

  statusItem = vscode.window.createStatusBarItem(vscode.StatusBarAlignment.Right, 100);
  statusItem.command = 'sif.startChat';
  statusItem.text = 'sif';
  statusItem.tooltip = 'Start sif with live editor context';
  statusItem.show();
  context.subscriptions.push(statusItem);

  update();
}

async function startChat(contextFile) {
  await writeEditorContext(contextFile);

  const config = vscode.workspace.getConfiguration('sif');
  const command = config.get('command', 'sif');
  const terminalName = config.get('terminalName', 'sif');
  const workspaceFolder = vscode.workspace.workspaceFolders?.[0]?.uri.fsPath;

  if (!terminal || terminal.exitStatus !== undefined) {
    terminal = vscode.window.createTerminal({
      name: terminalName,
      cwd: workspaceFolder,
      env: {
        SIF_VSCODE_CONTEXT_FILE: contextFile
      }
    });
    terminalStarted = false;
  }

  terminal.show();
  if (!terminalStarted) {
    terminal.sendText(command, true);
    terminalStarted = true;
  }
}

async function writeEditorContext(contextFile) {
  const editor = vscode.window.activeTextEditor;
  const maxSelectionChars = vscode.workspace.getConfiguration('sif').get('maxSelectionChars', 6000);

  const payload = lastEditorPayload ?? {
    source: 'vscode',
    updatedAt: new Date().toISOString(),
    workspaceFolder: vscode.workspace.workspaceFolders?.[0]?.uri.fsPath ?? null,
    file: null,
    line: null,
    column: null,
    selectedText: null,
    debugConsoleFile
  };

  if (editor) {
    const selection = editor.selection;
    const selectedText = editor.document.getText(selection);

    payload.file = editor.document.uri.scheme === 'file'
      ? editor.document.uri.fsPath
      : editor.document.uri.toString();
    payload.line = selection.active.line + 1;
    payload.column = selection.active.character + 1;
    payload.selectedText = trim(selectedText, maxSelectionChars);
    payload.updatedAt = new Date().toISOString();
    payload.workspaceFolder = vscode.workspace.workspaceFolders?.[0]?.uri.fsPath ?? null;
    lastEditorPayload = payload;
  } else {
    payload.updatedAt = new Date().toISOString();
  }

  await fs.promises.writeFile(contextFile, JSON.stringify(payload, null, 2), 'utf8');
}

// Mirrors debug console output into a file sif can read. Output reaches the
// debug console as DAP 'output' events, so a tracker on every debug type sees
// the same text the user sees.
function registerDebugConsoleCapture(context) {
  let pending = [];
  let flushTimer;
  let activeTopLevelSessions = 0;

  const flush = () => {
    clearTimeout(flushTimer);
    flushTimer = undefined;
    if (pending.length === 0) {
      return;
    }

    const chunk = pending.join('');
    pending = [];
    try {
      fs.appendFileSync(debugConsoleFile, chunk, 'utf8');
      const size = fs.statSync(debugConsoleFile).size;
      if (size > MAX_DEBUG_CONSOLE_BYTES) {
        const content = fs.readFileSync(debugConsoleFile, 'utf8');
        fs.writeFileSync(debugConsoleFile, '...[earlier output truncated]\n' + content.slice(-MAX_DEBUG_CONSOLE_BYTES / 2), 'utf8');
      }
    } catch {
      // Capture is best effort; never disturb the debug session.
    }
  };

  const append = text => {
    pending.push(text);
    if (!flushTimer) {
      flushTimer = setTimeout(flush, 250);
    }
  };

  const describeException = async (session, threadId) => {
    try {
      const info = await session.customRequest('exceptionInfo', { threadId });
      const lines = [`--- stopped on exception: ${info.exceptionId ?? ''} ${info.description ?? ''}`.trimEnd() + ' ---'];
      if (info.details?.message) {
        lines.push(info.details.message);
      }
      if (info.details?.stackTrace) {
        lines.push(info.details.stackTrace);
      }
      append(lines.join('\n') + '\n');
    } catch {
      append('--- stopped on exception (no exception details from debug adapter) ---\n');
    }
  };

  context.subscriptions.push(
    vscode.debug.registerDebugAdapterTrackerFactory('*', {
      createDebugAdapterTracker(session) {
        const isTopLevel = !session.parentSession;
        return {
          onWillStartSession() {
            // A fresh run starts a fresh log, unless another run (e.g. a
            // compound launch) is still writing to it.
            if (isTopLevel && activeTopLevelSessions++ === 0) {
              pending = [];
              try {
                fs.writeFileSync(debugConsoleFile, '', 'utf8');
              } catch {
              }
            }
            append(`--- debug session started: ${session.name} (${session.type}) at ${new Date().toISOString()} ---\n`);
          },
          onDidSendMessage(message) {
            if (message.type !== 'event') {
              return;
            }

            if (message.event === 'output') {
              const category = message.body?.category ?? 'console';
              const output = message.body?.output;
              if (category !== 'telemetry' && output) {
                append(output);
              }
            } else if (message.event === 'stopped' && message.body?.reason === 'exception') {
              describeException(session, message.body.threadId);
            } else if (message.event === 'exited') {
              append(`--- program exited with code ${message.body?.exitCode ?? 'unknown'} ---\n`);
            }
          },
          onError(error) {
            append(`--- debug adapter error: ${error?.message ?? error} ---\n`);
          },
          onWillStopSession() {
            append(`--- debug session ended: ${session.name} at ${new Date().toISOString()} ---\n`);
            if (isTopLevel) {
              activeTopLevelSessions = Math.max(0, activeTopLevelSessions - 1);
            }
            flush();
          }
        };
      }
    }),
    { dispose: flush }
  );
}

function trim(value, maxChars) {
  if (!value || maxChars <= 0) {
    return null;
  }

  if (value.length <= maxChars) {
    return value;
  }

  return value.slice(0, maxChars) + `\n...[truncated ${value.length - maxChars} chars]`;
}

function deactivate() {}

module.exports = {
  activate,
  deactivate
};

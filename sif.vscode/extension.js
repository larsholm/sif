const fs = require('fs');
const path = require('path');
const vscode = require('vscode');

let terminal;
let terminalStarted = false;
let statusItem;
let lastEditorPayload;

function activate(context) {
  const contextFile = path.join(context.globalStorageUri.fsPath, 'editor-context.json');
  fs.mkdirSync(context.globalStorageUri.fsPath, { recursive: true });
  context.environmentVariableCollection.replace('SIF_VSCODE_CONTEXT_FILE', contextFile);

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
    selectedText: null
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

# sif VS Code Extension

This extension starts `sif` in a VS Code integrated terminal and keeps the active editor context available to the running chat session.

## Commands

- `sif: Start Chat With Editor Context`
- `sif: Update Editor Context`
- `sif: Show Context File`

The extension writes a small JSON file containing the active file, cursor line, cursor column, and current selection. The `sif: Start Chat With Editor Context` terminal is launched with `SIF_VSCODE_CONTEXT_FILE` pointing at that file, and regular integrated terminals opened after the extension activates receive the same environment variable.

When focus moves from the editor to the terminal, the extension preserves the last editor snapshot instead of clearing it, so `sif` can still read the selection while you type in chat.

## Settings

- `sif.command`: command used to start the CLI, default `sif`
- `sif.terminalName`: integrated terminal name, default `sif`
- `sif.maxSelectionChars`: maximum selection text included in context, default `6000`

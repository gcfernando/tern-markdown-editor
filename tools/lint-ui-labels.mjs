import fs from 'node:fs';
import path from 'node:path';
import assert from 'node:assert/strict';
import { fileURLToPath } from 'node:url';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const small = new Set(['a', 'an', 'and', 'as', 'at', 'for', 'from', 'in', 'of', 'or', 'the', 'to', 'with']);
const proper = new Set(['Tern', 'Markdown', 'CommonMark', 'GitHub', 'Copilot', 'Windows', 'Source', 'Split', 'Reading',
  'Library', 'Inspector', 'Quick', 'Start', 'File', 'Explorer', 'Recycle', 'Bin', 'Credential', 'Manager']);
const inputCommands = new Set(['Help', 'AIConnections', 'AskAI', 'Reset', 'ClearRecent', 'Rename', 'MoveDocument',
  'NewFromTemplate', 'OpenFile', 'OpenUrl', 'NewWorkspaceFile', 'NewFolder', 'Print', 'OpenFolder', 'OpenWorkspace',
  'CreateWorkspace', 'PromoteFolder', 'SaveAs', 'ReloadFromDisk', 'ReviewSaveFragments', 'ReopenWithEncoding',
  'ChangeEncoding', 'ChangeLineEnding', 'QuickOpen', 'CommandPalette', 'GoToLine', 'GoToHeading', 'Find', 'Replace',
  'FindInWorkspace', 'ShowHistory', 'Welcome', 'InsertLink', 'InsertTable', 'InsertImage', 'PasteImage', 'ExportAs',
  'Settings', 'Shortcuts', 'About']);

function words(text) {
  return text.replace(/\{[^}]*\}/g, '').match(/[A-Za-z][A-Za-z']*/g) ?? [];
}

function casing(text, title) {
  return words(text).some((word, index) => title
    ? (!small.has(word.toLowerCase()) || index === 0) && /^[a-z]/.test(word)
    : index > 0 && /^[A-Z][a-z]/.test(word) && !proper.has(word));
}

export function lintSource(file, source) {
  const issues = [];
  function check(match, text, title, role) {
    if (!text || text.startsWith('{') || /^_.$/.test(text) || !/[A-Za-z]/.test(text)) return;
    const line = source.slice(0, match.index).split('\n').length;
    if (casing(text.replace(/_/g, ''), title)) issues.push(`${file}:${line}: ${role} casing: ${text}`);
    if (/ [\u2013-] /.test(text)) issues.push(`${file}:${line}: use an em dash separator: ${text}`);
  }
  if (file.endsWith('CommandRegistry.cs')) {
    for (const match of source.matchAll(/new\(CommandIds\.(\w+), "([^"]+)"/g)) {
      check(match, match[2], true, 'command');
      if (inputCommands.has(match[1]) && !match[2].endsWith('\u2026'))
        issues.push(`${file}: ${match[1]} opens an input surface and needs an ellipsis`);
    }
  }
  if (file.endsWith('.axaml')) {
    for (const match of source.matchAll(/<MenuItem\b[^>]*\bHeader="([^"]*)"[^>]*>/g)) check(match, match[1], true, 'menu');
    for (const match of source.matchAll(/<(?:Button|ToggleButton)\b[^>]*\bContent="([^"]*)"[^>]*>/g)) check(match, match[1], false, 'button');
  } else {
    for (const match of source.matchAll(/(?:new Button|Button\s+\w+\s*=\s*new\(\))\s*\{[^{}]*?\bContent\s*=\s*"([^"]*)"/g))
      check(match, match[1], false, 'button');
    for (const match of source.matchAll(/DialogParts\.Setup(?:Wizard)?\([^,\n]+,\s*\$?"([^"]*)"/g))
      check(match, match[1], false, 'window');
    for (const match of source.matchAll(/new MenuItem\s*\{[^{}]*?\bHeader\s*=\s*"([^"]*)"/g))
      check(match, match[1], true, 'menu');
  }
  return issues;
}

if (process.argv.includes('--self-test')) {
  assert.ok(lintSource('CommandRegistry.cs', 'new(CommandIds.OpenFile, "Open files", "File")').length >= 2);
  assert.ok(lintSource('Window.cs', 'var b = new Button { Content = "Save Changes" };').length > 0);
  assert.ok(lintSource('Window.cs', 'DialogParts.Setup(this, "Version history \u2013 example.md", 600, 400);').length > 0);
  assert.deepEqual(lintSource('Window.cs', 'var b = new Button { Content = "Open selected PDF" };'), []);
  console.log('UI-label lint regression checks passed.');
} else {
  function files(directory) {
    return fs.readdirSync(directory, { withFileTypes: true }).flatMap(entry => {
      const name = path.join(directory, entry.name);
      return entry.isDirectory() ? (['bin', 'obj'].includes(entry.name) ? [] : files(name))
        : /\.(cs|axaml)$/.test(entry.name) ? [name] : [];
    });
  }
  const inputs = [
    path.join(root, 'src', 'Tern.App', 'Commands', 'CommandRegistry.cs'),
    ...files(path.join(root, 'src', 'Tern.Desktop', 'Views'))
  ];
  const issues = inputs.flatMap(file => lintSource(path.relative(root, file), fs.readFileSync(file, 'utf8')));
  if (issues.length) { console.error(issues.join('\n')); process.exitCode = 1; }
  else console.log(`UI-label lint: zero violations across ${inputs.length} command/view sources.`);
}

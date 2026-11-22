// Splits a doctor fix text into prose and a command that gets its own copy block.
const COMMAND = /^(winget|git|gh|choco|scoop|npm|dotnet|cmd|powershell|pwsh|mklink)\b/i;

/**
 * Fix texts are prose that may end in a command ("Install Git LFS: winget install GitHub.GitLFS", "Run: gh auth login").
 * The command part gets its own copyable block.
 */
export function splitFix(fix: string): { text: string | null; command: string | null } {
  const trimmed = fix.trim();
  if (COMMAND.test(trimmed)) return { text: null, command: trimmed };
  const colon = trimmed.lastIndexOf(": ");
  if (colon > 0) {
    const tail = trimmed.slice(colon + 2).trim();
    if (COMMAND.test(tail)) {
      const head = trimmed.slice(0, colon).trim();
      return { text: /^(run|use)$/i.test(head) ? null : `${head}:`, command: tail };
    }
  }
  return { text: trimmed, command: null };
}

#!/bin/bash
# Install pre-commit hook for CSharpier formatting enforcement.
# Run once per clone, and re-run after updates: bash scripts/install-hooks.sh

set -e

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
TARGET="$REPO_ROOT/.git/hooks/pre-commit"

if [ -f "$TARGET" ]; then
    echo "Updating existing pre-commit hook..."
else
    echo "Installing pre-commit hook..."
fi

cat > "$TARGET" << 'HOOK'
#!/bin/bash
# Pre-commit hook: format staged .cs files with CSharpier (repo-pinned via
# .config/dotnet-tools.json) and re-stage the result. Formatting failures
# (e.g. unparsable C#) abort the commit — no silent no-op.

set -e

if ! dotnet csharpier --version &>/dev/null; then
    echo "[pre-commit] WARNING: CSharpier not found. Run: dotnet tool restore"
    exit 0
fi

staged=$(git diff --cached --name-only --diff-filter=ACMR -- '*.cs')
if [ -z "$staged" ]; then
    exit 0
fi

echo "[pre-commit] Formatting staged .cs files..."
echo "$staged" | while IFS= read -r f; do echo "  $f"; done
dotnet csharpier format $staged
echo "$staged" | xargs git add
echo "[pre-commit] Done."
exit 0
HOOK

chmod +x "$TARGET"
echo "Pre-commit hook installed."

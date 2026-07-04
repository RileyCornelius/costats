# Insights Card CLI

Generate a shareable Claude Code Insights card from `report.html` ([full docs](../tools/insights-cli/README.md)):

<p align="center">
  <img src="../tools/insights-cli/assets/costats-insights.png" alt="costats insights card example" width="400" />
</p>

```powershell
npx costats ccinsights
```

By default it reads `~/.claude/usage-data/report.html` and writes the PNG to `~/.costats/images/costats-insights.png`.

Optional flags:
- `--json <path>` to export the extracted JSON alongside the PNG.
- `--no-open` to avoid opening the image viewer.

Requires an active Claude Code OAuth login (uses `~/.claude/.credentials.json`).
First run may download a Playwright Chromium binary for rendering.

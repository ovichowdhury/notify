---
name: build-css
description: Rebuild or watch the Tailwind CSS after editing Razor views or Styles/app.css. Use when styling looks stale, a new Tailwind class does not apply, or the Tailwind CLI is missing.
---

# Tailwind CSS build

Source: `src/Notify.Web/Styles/app.css` (tokens and component classes such as `.btn`, `.card`, `.input`).
Output: `src/Notify.Web/wwwroot/css/tailwind.css` (minified, committed).

## Automatic

`dotnet build` runs the MSBuild target `BuildTailwindCss` whenever `scripts/tailwindcss.exe` exists. If it does
not exist, the committed CSS is used and new classes will **not** appear until the CLI is installed.

## Install the CLI (once per machine, git-ignored)

```powershell
powershell -ExecutionPolicy Bypass -File scripts/get-tailwind.ps1
```

## Manual build / watch (from the repository root)

```bash
scripts/tailwindcss.exe -c src/Notify.Web/tailwind.config.js -i src/Notify.Web/Styles/app.css -o src/Notify.Web/wwwroot/css/tailwind.css --minify
scripts/tailwindcss.exe -c src/Notify.Web/tailwind.config.js -i src/Notify.Web/Styles/app.css -o src/Notify.Web/wwwroot/css/tailwind.css --watch
```

## Rules

- Tailwind scans `Views/**/*.cshtml` and `wwwroot/js/**/*.js` (see `tailwind.config.js`). Class names built
  dynamically in C# (string concatenation) are not detected; write the full class string in a switch or a view, as
  `_StatusBadge.cshtml` does.
- Add new reusable component classes to `Styles/app.css` under `@layer components`, not to views or inline styles.
- Do not reintroduce Bootstrap or the Tailwind Play CDN.

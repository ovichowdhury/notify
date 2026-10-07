# Security Policy

## Reporting a vulnerability

Please do **not** open a public GitHub issue for security problems.

Email **nahid@erainfotechbd.com** with a description of the issue, steps to reproduce, and the impact you
believe it has. You will receive an acknowledgement within 5 working days. We ask that you give us a reasonable
window to ship a fix before any public disclosure.

## Scope

Reports are welcome for anything in this repository, in particular:

- Cross-tenant data access (one account reading or changing another account's SMTP settings, campaigns,
  recipients or reports)
- Authentication and session weaknesses
- Injection through uploaded CSV/XLSX files or email templates (HTML injection, header injection into SMTP)
- Exposure of stored SMTP credentials
- Unsafe file handling in logo uploads

## Supported versions

Only the latest commit on `main` receives security fixes.

## Deployment notes for operators

- Change `SeedAdmin:Password` before the first production start, or supply it via the `SeedAdmin__Password`
  environment variable.
- Keep `App_Data/keys` private and backed up: it holds the Data Protection keys that encrypt every stored SMTP
  password. Losing it means re-entering all SMTP passwords; leaking it exposes them.
- The app serves plain HTTP on port 5600. Put it behind a reverse proxy that terminates TLS.

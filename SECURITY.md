# Security policy

## Reporting a vulnerability

Please **don't open a public issue** for security problems. Report them privately with [GitHub's private vulnerability reporting](https://github.com/Cyssel/Gameroom/security/advisories/new).

Please include what's affected, how to reproduce it and what an attacker could do. You'll get an answer as soon as possible, and credit in the release notes if you'd like.

## Supported versions

Only the [latest release](https://github.com/Cyssel/Gameroom/releases/latest) gets security fixes. Gameroom updates itself, so most users are on it.

## How Gameroom protects your data

- **No servers of its own.** Accounts and cloud saves run on the user's own Supabase project.
- **Only the publishable key is used.** The app refuses secret and service-role keys. Row level security limits every account to its own profile, save list and files ([`supabase/setup.sql`](supabase/setup.sql)).
- **The cloud session is encrypted** for the current Windows user with DPAPI (`cloud-session.dat`).
- **Updates are checked** against the SHA-256 that GitHub publishes for the installer.
- **No telemetry.**

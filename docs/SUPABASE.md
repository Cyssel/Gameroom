# Your own Gameroom server (Supabase)

Gameroom has no servers of its own. Accounts and cloud saves run on a **[Supabase](https://supabase.com) project that you create and own**, for free. This guide takes about 5 minutes.

<p align="center"><img src="images/server.jpg" alt="Connect your server in Gameroom" width="480"></p>

## What you get

- **A Gameroom account** (email, password and username).
- **Cloud saves.** Every save backup Gameroom makes is uploaded to your server. On another PC, or after reinstalling Windows, sign in and your saves come back.
- **Privacy by design.** Your saves only go to your project. Row level security (RLS) lets every account read and change only its own data.

Everything else in Gameroom (library, live detection, playtime, local backups, achievements) works without a server. This is optional.

## 1. Create a project

1. Sign up at [supabase.com](https://supabase.com). The free plan is enough.
2. Click **New project**:
   - Pick any name, for example `gameroom`.
   - Set a database password, and save it somewhere safe.
   - Choose the region closest to you.
3. Wait a minute or two until the project is ready.

## 2. Prepare the database

1. In your project, open **SQL Editor** → **New query**.
2. Paste the whole contents of [`supabase/setup.sql`](../supabase/setup.sql).
3. Click **Run**. You should see *Success. No rows returned*.

The script creates:

| What | Purpose |
|---|---|
| `public.profiles` | One profile per account: username and play stats. It's created automatically when someone signs up. |
| `public.cloud_saves` | The list of save backups uploaded by Gameroom. |
| Storage bucket `saves` (private) | The backup archives, in `saves/<user id>/<game>/<backup>.zip`. |
| RLS policies | Every account can only see and change its own profile, list and files. |

Running it again is safe, for example after a Gameroom update that changes the database.

## 3. Choose how accounts are confirmed

By default, Supabase asks new users to confirm their email. Its built-in email service only sends mail to the members of your project, and only a few emails per hour. Pick one option:

- **Just you, or people you trust (simplest):** go to **Authentication → Sign In / Providers → Email** and turn off **Confirm email**. New accounts can sign in right away.
- **Anyone can sign up:** keep confirmation on and set up your own email provider in **Authentication → Emails → SMTP Settings**. Any SMTP service works.

## 4. Copy the project URL and the publishable key

You can find both by clicking **Connect** at the top of the dashboard, or in:

- **Project Settings → Data API**: the **Project URL**, like `https://abcd1234.supabase.co`.
- **Project Settings → API Keys**: the **publishable key**, which starts with `sb_publishable_`. On older projects, use the **anon** key.

> [!WARNING]
> Never use the **secret key** (`sb_secret_…`) or the legacy **service_role** key. They give full access to your project and bypass every security rule. Gameroom refuses them, and you should never share them.

The publishable key is meant to be public: it only allows what the RLS policies allow.

## 5. Connect Gameroom

1. Open Gameroom and go to **Account** (the person icon in the sidebar).
2. Paste the **Project URL** and the **publishable key**, then click **Connect**. Gameroom checks that the project answers and that the database is ready.
3. Create your account and sign in.

From then on, every save backup is uploaded automatically. You can also click **Sync now** in **Account**.

To use another project later, sign out and click **Change server**.

## Sharing your server

Family and friends can use your server too: they enter the same URL and publishable key in their Gameroom and create their own accounts.

Each account only sees its own saves inside Gameroom. Keep in mind, though, that you are the owner of the project: you can see all the data in the Supabase dashboard. Only share your server with people who trust you.

## Free plan limits

The Supabase free plan includes about 500 MB of database, 1 GB of file storage and a 50 MB limit per file. Gameroom keeps it small:

- **In the cloud:** only the **5 most recent backups** of each game are kept. Older ones are deleted when new ones arrive.
- **On each PC:** only the **3 most recent backups** of each game are downloaded.

Free projects are **paused after a week without activity**. If that happens, open the dashboard and click **Restore project**. Your data stays there.

## Troubleshooting

| Gameroom says | What to do |
|---|---|
| *The server answers, but its database isn't ready* | Run `supabase/setup.sql` (step 2). |
| *That address doesn't answer like a Supabase project* | Copy the Project URL again (step 4). It must start with `https://`. |
| *The key isn't valid for that project* | Copy the publishable key (or the anon key) from the same project. |
| *Confirm your email before signing in* / *The confirmation email couldn't be sent* | See step 3: turn off confirmation, or set up SMTP. |
| *Couldn't reach your server* | Check your internet connection. If the project was paused, restore it in the dashboard. |

## Deleting data

- **Delete one account:** go to **Authentication → Users**, then delete the user. This removes the account's profile and save list. Its files stay in **Storage → saves → `<user id>`**, so delete that folder too.
- **Delete everything:** delete the project in **Project Settings → General**.

## Local Supabase

For development you can also run Supabase locally with the [Supabase CLI](https://supabase.com/docs/guides/cli). Gameroom accepts `http://127.0.0.1:54321` as the project URL. Plain `http` is only allowed for local addresses.

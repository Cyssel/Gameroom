-- ============================================================================
--  Gameroom: database for accounts and cloud saves
-- ============================================================================
--  Run this whole file once in your Supabase project:
--  Dashboard -> SQL Editor -> New query -> paste -> Run.
--  It can be run again safely (for example after a Gameroom update).
--
--  What it creates:
--    * public.profiles     one profile per account (username and play stats)
--    * public.cloud_saves  index of the save backups uploaded by Gameroom
--    * storage bucket "saves" (private) with the backup archives
--  Row level security makes every account see and change only its own data.
-- ============================================================================

-- ---------------------------------------------------------------------------
--  Profiles
-- ---------------------------------------------------------------------------
create table if not exists public.profiles (
    id uuid primary key references auth.users (id) on delete cascade,
    username text not null check (char_length(username) between 2 and 32),
    total_playtime_seconds bigint not null default 0 check (total_playtime_seconds >= 0),
    games_count integer not null default 0 check (games_count >= 0),
    created_at timestamptz not null default now(),
    updated_at timestamptz not null default now()
);

create unique index if not exists profiles_username_key on public.profiles (lower(username));

alter table public.profiles enable row level security;

drop policy if exists "Users read their own profile" on public.profiles;
create policy "Users read their own profile"
    on public.profiles for select to authenticated
    using (id = (select auth.uid()));

drop policy if exists "Users update their own profile" on public.profiles;
create policy "Users update their own profile"
    on public.profiles for update to authenticated
    using (id = (select auth.uid()))
    with check (id = (select auth.uid()));

revoke all on public.profiles from anon;
grant select, update (username, total_playtime_seconds, games_count, updated_at) on public.profiles to authenticated;

-- The profile is created with the account. The username comes from the sign up form and is made unique if taken.
create or replace function public.handle_new_user()
returns trigger
language plpgsql
security definer
set search_path = ''
as $$
declare
    base text := coalesce(nullif(trim(new.raw_user_meta_data ->> 'username'), ''), split_part(new.email, '@', 1));
    candidate text;
begin
    base := left(base, 26);
    if char_length(base) < 2 then
        base := 'jugador';
    end if;

    candidate := base;
    while exists (select 1 from public.profiles where lower(username) = lower(candidate)) loop
        candidate := base || floor(random() * 10000)::int;
    end loop;

    insert into public.profiles (id, username) values (new.id, candidate);
    return new;
end;
$$;

revoke execute on function public.handle_new_user() from public, anon, authenticated;

drop trigger if exists on_auth_user_created on auth.users;
create trigger on_auth_user_created
    after insert on auth.users
    for each row execute function public.handle_new_user();

-- ---------------------------------------------------------------------------
--  Cloud saves
-- ---------------------------------------------------------------------------
create table if not exists public.cloud_saves (
    id uuid primary key default gen_random_uuid(),
    user_id uuid not null default auth.uid() references auth.users (id) on delete cascade,
    -- Identifies the game across PCs: "steam:<app id>" or "name:<normalized name>".
    game_key text not null,
    game_name text not null,
    steam_app_id text,
    object_path text not null,
    size bigint not null check (size >= 0),
    file_count integer not null check (file_count >= 0),
    content_hash text,
    trigger text not null,
    machine text,
    created_at timestamptz not null,
    uploaded_at timestamptz not null default now(),
    unique (user_id, object_path)
);

create index if not exists cloud_saves_user_game_idx on public.cloud_saves (user_id, game_key, created_at desc);

alter table public.cloud_saves enable row level security;

drop policy if exists "Users manage their own cloud saves" on public.cloud_saves;
create policy "Users manage their own cloud saves"
    on public.cloud_saves for all to authenticated
    using (user_id = (select auth.uid()))
    with check (user_id = (select auth.uid()));

revoke all on public.cloud_saves from anon;
grant select, insert, update, delete on public.cloud_saves to authenticated;

-- ---------------------------------------------------------------------------
--  Storage: private bucket with the save archives, one folder per account
-- ---------------------------------------------------------------------------
insert into storage.buckets (id, name, public, file_size_limit, allowed_mime_types)
values ('saves', 'saves', false, 52428800, array['application/zip'])
on conflict (id) do nothing;

drop policy if exists "Users manage save files in their own folder" on storage.objects;
create policy "Users manage save files in their own folder"
    on storage.objects for all to authenticated
    using (bucket_id = 'saves' and (storage.foldername(name))[1] = (select auth.uid())::text)
    with check (bucket_id = 'saves' and (storage.foldername(name))[1] = (select auth.uid())::text);

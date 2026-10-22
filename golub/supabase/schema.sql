-- ============================================================================
--  Голубь + Supabase: схема базы и правила доступа (RLS)
--
--  Как применить:
--    1. Открой https://supabase.com/dashboard → свой проект
--    2. Слева SQL Editor → New query
--    3. Вставь ВЕСЬ этот файл → Run
--
--  Файл можно запускать повторно — он ничего не ломает.
-- ============================================================================

-- ---------------------------------------------------------------- профили
-- Имя для входа (username) и человеческое имя для отображения (display_name).
create table if not exists public.profiles (
    id           uuid primary key references auth.users(id) on delete cascade,
    username     text not null,
    display_name text not null,
    last_seen    timestamptz not null default now()
);

-- уникальность имени для входа без учёта регистра
create unique index if not exists profiles_username_lc_idx
    on public.profiles (lower(username));

alter table public.profiles enable row level security;

drop policy if exists "profiles_select_authenticated" on public.profiles;
create policy "profiles_select_authenticated" on public.profiles
    for select to authenticated using (true);

drop policy if exists "profiles_update_self" on public.profiles;
create policy "profiles_update_self" on public.profiles
    for update to authenticated
    using (id = auth.uid()) with check (id = auth.uid());

-- ---------------------------------------------------------------- сообщения
create table if not exists public.messages (
    id         bigint generated always as identity primary key,
    mid        uuid not null unique,              -- клиентский номер, защита от дублей
    sender     uuid not null references auth.users(id) on delete cascade,
    recipient  uuid not null references auth.users(id) on delete cascade,
    body       text not null check (char_length(body) between 1 and 4000),
    created_at timestamptz not null default now()
);

create index if not exists messages_sender_idx    on public.messages (sender, id);
create index if not exists messages_recipient_idx on public.messages (recipient, id);

alter table public.messages enable row level security;

-- вижу только свои переписки (то, что я отправил, и то, что мне отправили)
drop policy if exists "messages_select_own" on public.messages;
create policy "messages_select_own" on public.messages
    for select to authenticated
    using (sender = auth.uid() or recipient = auth.uid());

-- отправлять можно только от своего имени
drop policy if exists "messages_insert_own" on public.messages;
create policy "messages_insert_own" on public.messages
    for insert to authenticated
    with check (sender = auth.uid());

-- изменять и удалять отправленное через API нельзя (политик нет = запрещено)

-- ---------------------------------------------------------------- прочитано
create table if not exists public.reads (
    user_id    uuid not null references auth.users(id) on delete cascade,
    peer_id    uuid not null references auth.users(id) on delete cascade,
    last_id    bigint not null default 0,
    updated_at timestamptz not null default now(),
    primary key (user_id, peer_id)
);

alter table public.reads enable row level security;

-- свой маркер читаю и правлю сам; чужой маркер нужен, чтобы видеть «прочитано»
drop policy if exists "reads_select_own" on public.reads;
create policy "reads_select_own" on public.reads
    for select to authenticated
    using (user_id = auth.uid() or peer_id = auth.uid());

drop policy if exists "reads_insert_own" on public.reads;
create policy "reads_insert_own" on public.reads
    for insert to authenticated with check (user_id = auth.uid());

drop policy if exists "reads_update_own" on public.reads;
create policy "reads_update_own" on public.reads
    for update to authenticated
    using (user_id = auth.uid()) with check (user_id = auth.uid());

-- ------------------------------------------------- профиль при создании юзера
-- Когда ты создаёшь аккаунт (в панели или через admin.py), профиль появляется сам.
create or replace function public.handle_new_user()
returns trigger
language plpgsql
security definer set search_path = public
as $$
begin
    insert into public.profiles (id, username, display_name)
    values (
        new.id,
        coalesce(
            new.raw_user_meta_data->>'username',
            split_part(new.email, '@', 1)
        ),
        coalesce(
            new.raw_user_meta_data->>'display_name',
            new.raw_user_meta_data->>'username',
            split_part(new.email, '@', 1)
        )
    )
    on conflict (id) do update
        set username = excluded.username,
            display_name = excluded.display_name;
    return new;
end;
$$;

drop trigger if exists on_auth_user_created on auth.users;
create trigger on_auth_user_created
    after insert on auth.users
    for each row execute function public.handle_new_user();

-- ============================================================================
--  Готово. Дальше:
--  1) Аутентификация → Providers → Email → выключи «Allow new users to sign up»
--     (это и есть запрет на самостоятельную регистрацию)
--  2) Аутентификация → Users → Add user — так создаёшь аккаунты вручную,
--     либо запускай admin.py со своего компьютера.
-- ============================================================================

# Calendar reminders (Telegram)

A separate host, `CalendarMcp.Reminders`, watches one account's calendar and sends
meeting information to a Telegram chat. It covers three things:

- a **morning digest** with every remaining meeting of the day — or a short
  "nothing today" note, so silence always means something is wrong,
- **alerts before each meeting**, at the lead times you configure,
- **on-demand answers** to `/today`, `/tomorrow` and `/week`.

It reads the calendar through the provider the account already uses, so the EWS
logic and its credential handling are shared with the MCP server rather than
re-implemented.

## Prerequisites

- A Telegram bot created with [@BotFather](https://t.me/BotFather), and its token.
- The numeric `chat_id` of the chat that should receive the messages. Write the
  bot any message and ask [@getidsbot](https://t.me/getidsbot) for it.
- The account must already work through the MCP server; the reminders host
  reuses the same `appsettings.json`.

## Configuration

Everything lives in the `Notifications` section of `appsettings.json`:

```json
"Notifications": {
  "Enabled": true,
  "AccountId": "exchange-work",
  "LeadMinutes": [60, 15],
  "LeadToleranceMinutes": 2,
  "TimeZone": "Europe/Moscow",
  "Digest": {
    "Enabled": true,
    "Time": "08:30:00",
    "Cutoff": "12:00:00",
    "WeekDays": 7
  },
  "Telegram": {
    "Enabled": true,
    "BotToken": "YOUR-BOT-TOKEN",
    "ChatId": "YOUR-CHAT-ID",
    "PollTimeoutSeconds": 50,
    "CommandsEnabled": true
  },
  "StateFile": "logs/reminder-state.json",
  "TickSeconds": 30,
  "RefreshMinutes": 5
}
```

| Setting | Meaning |
|---|---|
| `AccountId` | Account whose calendar is polled. Required. |
| `LeadMinutes` | Minutes before a meeting at which an alert is sent. |
| `LeadToleranceMinutes` | Slack on both edges of a window, so a late tick still fires. |
| `TimeZone` | IANA zone used for every window and every displayed time. |
| `Digest.Time` | Local time the morning digest goes out. |
| `Digest.Cutoff` | Local time after which the day's digest is abandoned. |
| `Digest.WeekDays` | How many days the weekly view spans. |
| `Telegram.ChatId` | The single chat that receives everything. |
| `Telegram.PollTimeoutSeconds` | Long-poll hold time when waiting for commands. |
| `StateFile` | Dedup bookkeeping; relative paths resolve under the config directory. |
| `TickSeconds` | How often reminders are evaluated. |
| `RefreshMinutes` | How often the calendar is actually re-read. Ticks in between reuse the cache. |

The bot token is a credential. `config/appsettings.json` is Git-ignored, but a
token pasted into a chat, a log or a terminal command is already exposed — issue
a fresh one with `/revoke` in BotFather and fill it in locally.

## Running it

```bash
# see what would be sent right now, without sending it
CalendarMcp.Reminders --once --dry-run

# print the coming week to the console, sends nothing and reads no chat
CalendarMcp.Reminders --print-week

# send today's digest now, without marking the day as done
CalendarMcp.Reminders --digest-now

# send a single test message
CalendarMcp.Reminders --test

# forget everything and start over
CalendarMcp.Reminders --clear-state

# include per-request HTTP logging and debug messages
CalendarMcp.Reminders --once --verbose
```

`--help` lists all switches. `--dry-run` only suppresses delivery when combined
with a mode that actually produces messages, so use it with `--once` or
`--digest-now`; `--print-week` never sends anything.

## Logs

The host writes one file per day, `config\logs\reminders-yyyyMMdd.log`, and keeps
the current and previous days. It also writes to the console, but a task-launched
instance runs hidden, so the file is where you look after the fact:

```
2026-10-15 08:30:01.114 +03:00 [Information] CalendarMcp.Reminders: Sent morning digest for 2026-10-15 (4 meetings)
2026-10-22 09:51:26.031 +03:00 [Information] CalendarMcp.Reminders: Sent morning digest for 2026-10-22 (0 meetings)
2026-10-22 09:51:26.034 +03:00 [Information] CalendarMcp.Reminders: Reminder host alive: 1 passes, 9 meetings cached, digest today: sent
```

`System.Net.Http.HttpClient` is filtered out unless `--verbose` is passed: with a
calendar poll and a command long-poll in the loop, one line per request buries
everything else.

An hourly `Reminder host alive` line exists so that a quiet day cannot be mistaken
for a stalled process: it reports how many passes have completed and what happened
to today's digest. The digest decision is also logged once when it is skipped —
`window closed` after the cutoff — so a missing message always has a stated reason.

## Running it at login

The host answers commands, so it must stay alive rather than fire every few
minutes. Register it as a per-user task:

```powershell
.\Register-ScheduledTask.ps1 `
  -Executable "C:\CalendarMcp\CalendarMcp.Reminders.exe" `
  -ConfigDirectory "C:\CalendarMcp\config"

Start-ScheduledTask -TaskName 'CalendarMcp.Reminders'
```

The task starts at logon, restarts three times on failure, and does not run
while the session is locked. It launches the executable through a hidden
PowerShell so no console window appears on the desktop. The script verifies that
the task really was registered and fails loudly otherwise, because a rejected
task definition otherwise leaves you believing in a host that is not running.

Publishing the host for production looks like the rest of the project:

```bash
dotnet publish src/CalendarMcp.Reminders/CalendarMcp.Reminders.csproj \
  --configuration Release --runtime win-x64 --self-contained true \
  --output ./publish/win-x64/reminders
```

Stop the task before republishing: a running host locks its own assemblies, and a
publish over locked files fails halfway through.

Remove the task again with:

```powershell
.\Unregister-ScheduledTask.ps1
.\Unregister-ScheduledTask.ps1 -ClearState -ConfigDirectory "C:\CalendarMcp\config"
```

## Message format

Morning digest and the weekly view share one layout, two lines per meeting, with
the link on its own line when the invitation carried one:

```
Сводка на ср 15.10

14:00–16:00  Конференция Zoom
   с кем: Цыпина, Лукьянова
   zoom: https://istk-ru.zoom.us/j/81987043693?pwd=...

16:00–17:00  Обсудить Datareon
   с кем: Лукьянова
```

A per-meeting alert adds a countdown line:

```
Через 15 мин

14:00–16:00  Конференция Zoom
   с кем: Цыпина
   zoom: https://istk-ru.zoom.us/j/81987043693?pwd=...
```

Long subjects and long attendee lists are truncated with an ellipsis so the
layout stays narrow enough for a phone screen. The weekly view is sent as one
message per day; a day that still overflows the Telegram limit is split again
with a continuation marker.

## Commands

| Command | Plain-text equivalent | Answer |
|---|---|---|
| `/today` | «встречи сегодня», «что на сегодня» | Meetings left today |
| `/tomorrow` | «встречи завтра» | All meetings tomorrow |
| `/week` | «что на неделе», «встречи на неделю» | Next seven days, one message per day |

Commands are ASCII because Telegram requires it; the Russian phrases are matched
as ordinary text. Anything unrecognised gets a short help reply.

## Who can talk to the bot

One chat is served, and `ChatId` is an allowlist, not a broadcast target. Any
message from a different chat is dropped before the text is even parsed, so a
stranger who finds the bot gets no answer and causes no calendar query. The
update offset still advances, so other people's messages cannot flood the queue.

The bot token is the real secret here, not the chat ID: with a leaked token
strangers can message the bot, but the allowlist still keeps your meetings away
from them. Issue a fresh token with `/revoke` in BotFather if it is ever exposed.

Serving more people is a different configuration: each of them needs their own
account, otherwise everyone would see the same calendar.

## How reminders avoid repeating themselves

The state file records, per meeting, which lead times have already fired, and
the dates on which a digest went out. A restart therefore does not resend
anything, and a second alert is still sent for a different lead time. The file is
written atomically, and a corrupt file is treated as empty state rather than
crashing the host.

The calendar itself is cached: one read per `RefreshMinutes` regardless of how
often reminders are evaluated. An empty result is cached too — otherwise a day
without meetings would turn every tick into another Exchange request.

## What is deliberately not here

- **All-day events and cancelled meetings** never produce an alert: one has no
  useful lead time and the other no longer happens.
- **Meetings you are not part of** are skipped, matched against the account
  address. On servers that return the organizer as a legacy X500 name and leave
  attendee lists empty there is nothing to compare against, so such meetings are
  kept rather than silently dropped.
- **Late alerts** — a meeting whose whole window has passed is dropped rather
  than sent, so a host that was off all morning does not dump a backlog when it
  starts.
- **A machine that is off** misses whatever fell in the gap. Telegram keeps
  unacknowledged commands for about a day, so commands sent while the machine was
  down are answered on the next start; reminders are not.

## Tests

`src/CalendarMcp.Tests/Reminders` covers period selection, the layout and its
truncation, digest scheduling, lead windows and their tolerance, deduplication
across restarts, configuration binding, Telegram payload parsing, and command
interpretation including the Russian phrases.

## Troubleshooting

**No messages arrive**
Run `CalendarMcp.Reminders --test`. If it reports success, delivery works and
the problem is scheduling — check that the meeting falls inside a lead window and
that the day was not already marked as digested. If it fails, the log names the
Telegram error.

**"Notifications is not configured"**
`BotToken` or `ChatId` is still empty in the `Notifications.Telegram` section.

**"Account … is not configured"**
`Notifications.AccountId` does not match an account ID from `list_accounts`.

**The bot answers nothing to `/week`**
Check the chat ID. A message from any other chat is dropped silently by design.

**The task does not start after logon**
Task Scheduler only runs it in an interactive session, so logging on through a
locked or disconnected RDP session leaves it stopped.

**Nothing appears in the log after a restart**
The task starts the executable through a hidden PowerShell; check
`config\logs\reminders-*.log` rather than looking for a window.
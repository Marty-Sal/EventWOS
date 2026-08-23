# EventWOS Notification Catalogue

Audit date: 2026-08-24, at commit `9f9a1ac`.

This is the single reference for what EventWOS sends, who receives it, what triggers it,
and what is still missing. It replaces "grep the handlers and hope".

---

## 1. What the audit found

The call-site sweep that finished at `9f9a1ac` answered one question: does every SignalR
push have a durable counterpart? It does. But that sweep could not see the opposite gap
-- **templates that exist and are seeded but that nothing ever triggers**. There are
nine, and three of them matter a great deal.

### 1.1 FIXED: the OTP was handed to the caller (account takeover)

**Status: closed in code. One flag remains the owner's call -- see the note at the end.**

`RequestPasswordResetHandler` and `RequestOtpHandler` both did this:

```csharp
var devOtp = _otpService.IsDevelopmentMode ? plaintext : null;
```

`Otp:IsDevelopmentMode` is `true` in `src/Api/appsettings.json` -- the base file, which
applies in production -- so both endpoints returned the plaintext OTP in the response
body, and `ForgotPassword.razor` printed it on screen.

The reset path was the lesser half. `VerifyOtpHandler` calls
`_jwtService.GenerateAccessToken`, so the **login** OTP path was a full sign-in bypass:
`POST /api/auth/request-otp` with any mobile number, read the code out of the response,
`POST /api/auth/verify-otp`, and you hold that user's access token. No password, no reset,
no email. Admin included.

The root cause was one flag doing two unrelated jobs. "Do not call the SMS provider" is a
delivery concern and genuinely should stay on until a provider is live. "Hand the
plaintext to the caller" is a credential-disclosure decision. They are now separate:

| Flag | Means | Default | Notes |
|---|---|---|---|
| `Otp:IsDevelopmentMode` | stub SMS: log the code instead of sending it | `true` | expected to stay `true` until SMS/WhatsApp is live; safe |
| `Otp:ExposeOtpInApiResponse` | return the plaintext OTP in the API response | `false` | dangerous; `true` only in `appsettings.Development.json` |

`Program.cs` additionally forces `ExposeOtpInApiResponse` to `false` whenever the hosting
environment is Production, and logs a startup line if configuration tried to enable it.
Configuration cannot switch this on in production -- not from appsettings, not from a
Railway variable, not by accident.

`StubSmsProvider` also used to log at Information and return `true`, which put "sent" in
the logs for a message that never left the process. It now logs a warning that says NOT
DELIVERED and returns `false`.

Pinned by `tests/Application.UnitTests/Auth/OtpExposureTests.cs`, including a guard that
reads `src/Api/appsettings.json` and fails if the exposure flag is ever `true` there.

**Still the owner's call:** `Otp__IsDevelopmentMode` on Railway can stay `true` -- it is
now only a delivery switch. While it is `true`, no SMS goes out, so password reset works
only for accounts that have an email address (SendGrid is live and the handler already
emails the code). Mobile-only crew cannot self-serve a reset until an SMS or WhatsApp
provider is live. To complete an OTP flow against the deployed app, read the code from the
Railway logs -- the stub logs it -- or from the email copy.

**Correction to an earlier recommendation.** An earlier draft of this document suggested
routing the OTP through the notification platform so it would inherit retries and delivery
tracking. That is wrong, and the reason matters: the durable outbox persists the rendered
token payload, so the platform would write the **plaintext OTP into the database** and
leave it there after use -- defeating the point of storing only a BCrypt hash in
`OtpRequests`. OTP delivery should stay synchronous and direct (the email path already is).
If OTP over WhatsApp is wanted later, it should call the provider inline, not enqueue.

### 1.2 Silent event changes

`UpdateEventCommand`, `UpdateEventShiftCommand` and `ArchiveEventShiftCommand` contain
zero notification code. So today:

- An admin moves an event's start time or venue: **nobody is told.** Crew arrive at the
  old time, at the old place. `EVENT_UPDATED` is seeded and unused.
- An admin changes a shift's hours or crew count: **nobody is told.** `SHIFT_CHANGED` is
  seeded and unused.
- An admin archives a shift that already has crew on it: **nobody is told**, and there is
  no template for it at all.

Of everything in this document, this is the gap most likely to make a person travel to
the wrong place at the wrong time.

### 1.3 No scheduler, so every reminder is dead code

The only `BackgroundService` in the solution is `NotificationWorker`, which drains the
outbox and the delivery queue. Nothing fires on a clock. That leaves four seeded
templates permanently unused: `VENDOR_EVENT_REMINDER`, `CREW_ASSIGNMENT_REMINDER`,
`EVENT_STARTING`, `ATTENDANCE_REMINDER`.

A reminder scheduler is genuinely separate work: it needs a due-time query, a
per-(assignment, kind) sent-marker so a restart does not re-send, and a decision on how
far ahead each reminder goes out.

### 1.4 No opt-out

There is no per-user notification preference anywhere -- no channel toggles, no quiet
hours, no unsubscribe. Recipients cannot turn any of this off. Tolerable while only
in-app and email are live; it stops being tolerable the day WhatsApp is switched on, both
for goodwill and because template-messaging providers expect an opt-out path.

### 1.5 Still pending from earlier work

- AiSensy/Meta WhatsApp credentials and campaign approval (needs the account owner).
- The frontend inbox component and bell badge were called out as unfinished in an earlier
  checkpoint. The backend is done; the UI wiring should be re-verified.

### 1.6 Standing instructions -- all present

Checked against the code, not from memory: 18+ DOB validation with auto-calculated age
(`RegisterCrewValidator`, `UpdateProfileCommand`, `User.CalculateAge`); profile photo and
ID proof document types; full-name `^[A-Za-z ]+$` and mobile `^\d{10}$` on both
registration paths; vendor invite template (`User.InviteMessageTemplate`, migration
`20260818231000`); document/file migrations (`20260817000000_AddFileDocuments`); vendor
shifts auto-creating the placeholder `EventAssignment` plus notification (done at
`ddeb125`); distinct completed events via `VendorEventParticipationRules`; 10-minute
session heartbeat grace (`SessionActivityRules.HeartbeatGrace`).

---

## 2. Channel policy

Three rules, applied consistently across the catalogue below.

1. **Urgent or irreversible news uses every allowed channel.** Approvals, rejections,
   revocations, money, cancellations. The recipient may not have the app open, and the
   news changes what they do next.
2. **Routine confirmations of something the recipient just watched happen are in-app
   only.** A vendor seeing "your crew accepted" seconds after the crew clicked accept
   does not need an email. Spending outbound channels on these is what teaches people to
   ignore them.
3. **Where a bespoke rich email already exists** (crew approval and rejection), the
   platform notification is in-app only, so the good email is not shadowed by a worse
   generic one.

Priority comes from `NotificationPolicy.DefaultPriority(templateCode)`.

---

## 3. The catalogue

Status key: **live** = wired and deployed; **silent by design** = deliberately sends
nothing, documented in code; **gap** = should send something and does not.

### 3.1 Account and access

| Scenario | Trigger | Recipients | Template | Channels | Status |
|---|---|---|---|---|---|
| Crew registers | `RegisterCrewHandler` | Admins + Managers, and the referring vendor | `REGISTRATION_PENDING_APPROVAL` | all | live |
| Vendor registers | `RegisterVendorHandler` | Admins + Managers | `REGISTRATION_PENDING_APPROVAL` | all | live |
| Account approved | `ApproveUserHandler` | the user | `ACCOUNT_APPROVED` | in-app (bespoke email already sent) | live |
| Account rejected | `RejectUserHandler` | the user | `ACCOUNT_REJECTED` | in-app (bespoke email already sent) | live |
| Password reset requested | `RequestPasswordResetHandler` | the user | none: sent inline, deliberately not via the outbox (1.1) | email now, SMS when a provider is live | live for accounts with email |
| Login OTP requested | `RequestOtpHandler` | the user | none: sent inline | SMS when a provider is live | blocked on a provider |
| Admin invites a user directly | no trigger exists | the invitee | `ACCOUNT_INVITED` | all | gap (feature not built) |
| User completes their profile | no trigger exists | Admins/Managers | `PROFILE_COMPLETED` | in-app | gap (low value - consider deleting the template) |
| Login from a new device | no trigger exists | the user | none | -- | proposed (see 4) |

### 3.2 Vendor engagement with an event

| Scenario | Trigger | Recipients | Template | Channels | Status |
|---|---|---|---|---|---|
| Vendor picked on a shift while creating an event | `CreateEventHandler` | the vendor | `VENDOR_EVENT_INVITED` | all | live |
| Vendor picked on a newly added shift | `AddEventShiftHandler` | the vendor | `VENDOR_EVENT_INVITED` | all | live |
| Vendor invited to an event (admin assign path) | `AssignCrewHandler`, vendor-only mode | the vendor | `VENDOR_EVENT_INVITED` | all | live |
| Vendor re-invited after a revoke or decline | `ReinviteVendorHandler` | the vendor | `VENDOR_EVENT_INVITED` | all | live |
| Vendor's invite revoked | `RevokeVendorInviteHandler` | the vendor | `VENDOR_INVITE_REVOKED` | all | live |
| Vendor accepts the event | `VendorRespondToInviteHandler` | Admins + Managers | `VENDOR_ACCEPTED_EVENT` | all | live |
| Vendor declines the event | `VendorRespondToInviteHandler` | Admins + Managers | `VENDOR_REJECTED_EVENT` | all | live |
| Standalone quota grant (Vendor Quotas panel) | `CreateVendorAllocationCommand` | -- | -- | -- | silent by design: budget change, not an invitation |

A vendor picked on several shifts of one event gets **one** invitation. The key is
`event:{eventId}:vendor-invited:{vendorId}` on both creation paths, because the template
carries nothing shift-specific and repeats would be word-for-word identical.

### 3.3 Crew staffing

| Scenario | Trigger | Recipients | Template | Channels | Status |
|---|---|---|---|---|---|
| Vendor invites one of their crew | `VendorAssignCrewHandler` | the crew member | `CREW_INVITATION` | all | live |
| Vendor invites a whole group | `VendorAssignGroupHandler` | each member who got a row | `CREW_INVITATION` | all | live |
| Admin or Manager assigns crew directly | `AssignCrewHandler` | the crew member | `CREW_ASSIGNMENT` | all | live |
| Crew accepts | `RespondAssignmentHandler` | their vendor, else Admins/Managers | `CREW_ACCEPTED_ASSIGNMENT` | in-app | live |
| Crew declines | `RespondAssignmentHandler` | their vendor, else Admins/Managers | `CREW_DECLINED_ASSIGNMENT` | all | live |
| Vendor forwards crew to the manager | `VendorReviewAssignmentHandler` | Admins + Managers | `ASSIGNMENT_PENDING_APPROVAL` | all | live |
| Vendor bypasses crew acceptance and forwards | `VendorDirectForwardHandler` | Admins + Managers | `ASSIGNMENT_PENDING_APPROVAL` | all | live |
| Vendor rejects crew | `VendorReviewAssignmentHandler` | the crew member | `CREW_ASSIGNMENT_REJECTED` | all | live |
| Manager approves crew (final) | `ManagerReviewAssignmentHandler` | the crew member | `CREW_ASSIGNMENT_APPROVED` | all | live |
| Manager rejects crew | `ManagerReviewAssignmentHandler` | the crew member | `CREW_ASSIGNMENT_REJECTED` | all | live |
| Vendor stage forwards crew onward | -- | the crew member | -- | -- | silent by design: approval is two-stage, only the final decision is news |
| Crew invite revoked | `VendorRevokeCrewInviteHandler` | the crew member | `CREW_INVITE_REVOKED` | all | live |

On keys: re-inviting resurrects the **same** assignment row, so every invite-style key
carries `DateTime.UtcNow.Ticks`. A static per-row key silently swallowed second
invitations -- fixed at `4fc4515`.

### 3.4 Event lifecycle

| Scenario | Trigger | Recipients | Template | Channels | Status |
|---|---|---|---|---|---|
| Manual announcement | `SendEventAnnouncementHandler` | chosen audience | `EVENT_ANNOUNCEMENT` | all | live |
| Event cancelled | `ChangeEventStatusHandler` | everyone assigned | `EVENT_CANCELLED` | all | live |
| Event time, venue or details changed | `UpdateEventCommand` | everyone assigned | `EVENT_UPDATED` | all | **gap - see 1.2** |
| Shift hours or crew count changed | `UpdateEventShiftCommand` | crew on that shift and their vendors | `SHIFT_CHANGED` | all | **gap - see 1.2** |
| Shift archived with crew on it | `ArchiveEventShiftCommand` | crew on that shift and their vendors | none yet | all | **gap - needs a template** |
| Event starting soon | needs a scheduler | everyone assigned | `EVENT_STARTING` | all | gap - see 1.3 |

### 3.5 Attendance

| Scenario | Trigger | Recipients | Template | Channels | Status |
|---|---|---|---|---|---|
| Check-in verified by vendor | `VerifyCheckInHandler` | the crew member | `CHECK_IN_VERIFIED` | in-app: they are standing there watching | live |
| Shift starts soon, check in | needs a scheduler | assigned crew | `ATTENDANCE_REMINDER` | all | gap - see 1.3 |
| Assignment reminder before the event | needs a scheduler | assigned crew | `CREW_ASSIGNMENT_REMINDER` | all | gap - see 1.3 |
| Vendor reminder before the event | needs a scheduler | the vendor | `VENDOR_EVENT_REMINDER` | all | gap - see 1.3 |
| Admin marks someone attended manually | `AdminMarkAttendedCommand` | the crew member | none yet | in-app | gap - low priority, but it changes their pay |
| No-show detected | no trigger exists | vendor + Managers | none | -- | proposed (see 4) |

### 3.6 Money

| Scenario | Trigger | Recipients | Template | Channels | Status |
|---|---|---|---|---|---|
| Payment approved | `UpdatePaymentStatusCommand` (`approve`) | the crew member | `PAYMENT_APPROVED` | all | live |
| Payment released or paid | `UpdatePaymentStatusCommand` (`pay`), `UpdatePayrollStatusCommand` | the crew member | `PAYROLL_RELEASED` | all | live |
| Payment rejected | `UpdatePaymentStatusCommand` (`reject`) | the crew member | `PAYMENT_REJECTED` | all | live |
| Payment record created | the three payroll creators | -- | -- | -- | silent by design: row is Pending, batch is Draft, nothing has happened yet |
| Payment put on hold | `UpdatePaymentStatusCommand` (`hold`) | -- | -- | -- | silent by design: internal review state that routinely flips back within minutes |
| Crew acknowledges a payment | `UpdatePaymentStatusCommand` (ack) | -- | -- | -- | silent by design: it is their own click |

### 3.7 Ratings

| Scenario | Trigger | Recipients | Template | Channels | Status |
|---|---|---|---|---|---|
| Crew rated after an event | `RateCrewCommand` | the crew member | none yet | in-app | gap - decide first whether crew should see their rating at all |

---

## 4. Proposed, not yet designed

Scenarios with no template and no trigger, listed so the decision is explicit rather than
forgotten:

- **New-device or new-location login**, to the user, as a security notice.
- **No-show detection** after a shift starts with no check-in, to the vendor and managers,
  since that is the moment staffing can still be fixed.
- **ID document expiry** ahead of an event, if expiry dates are ever captured.
- **Terms or policy update** requiring re-acceptance, to everyone.
- **Weekly digest** to vendors and managers, as the honest alternative to per-event
  reminder spam.

---

## 5. Recommended order of work

1. ~~Close the OTP hole~~ -- **done** (1.1). Remaining owner decision: whether to stand up
   an SMS/WhatsApp provider so mobile-only accounts can reset their own password.
2. **`EVENT_UPDATED` and `SHIFT_CHANGED`**, plus a shift-cancelled template (1.2). Cheap,
   and it stops people travelling to the wrong place at the wrong time.
3. **Opt-out and channel preferences** (1.4), before WhatsApp goes live rather than after.
4. **Reminder scheduler** (1.3), which lights up four templates at once.
5. WhatsApp credentials and the provider flip (1.5), once 3 is in place.

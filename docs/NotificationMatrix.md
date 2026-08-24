# EventWOS Notification Matrix

Every notification the system can send: what happens, what triggers it, who hears
about it, on which channels, and where tapping it lands.

Generated from the code, not from memory -- the source of each column is
NotificationTemplateCodes, NotificationPolicy, PushDeepLinks and the call sites
themselves. If a row here is wrong, the code changed and this file did not.

## How a channel is actually chosen

The channel list below is the POLICY -- a ceiling, not a promise. Four gates narrow
it, in order, and each one can only take channels away:

1. **Policy** -- is this type of news worth this channel at all (the table below)
2. **Template** -- is there an ACTIVE template row for that code + channel, which is
   how an admin switches a channel off for one notification type with no deploy
3. **Provider** -- is that channel's provider configured; with no WhatsApp keys the
   whole channel silently drops out rather than queueing messages that can only fail
4. **Recipient** -- do they have the contact detail it needs (an email address, a
   mobile number). Bell and Push need none: push addresses the USER and fans out
   across their registered browsers at send time.

**Today that means:** Bell always, Push wherever it is listed, Email where SendGrid
applies, and WhatsApp nowhere at all until Meta/AiSensy credentials are set.

## Status at a glance

- **30** notification types defined, all **30** with seeded templates
- **21** are wired to a real trigger and fire today
- **8** have templates and policy but nothing triggers them yet (marked DORMANT)
- **1** is deliberately outside the platform (the reset OTP)


## 1. Getting in -- account and registration

| Notification | Scenario | Triggered by | Who is told | Channels | Priority | Opens |
|---|---|---|---|---|---|---|
| `REGISTRATION_PENDING_APPROVAL` | Someone finishes signing up as crew or vendor and needs approving | Crew/vendor submits registration<br>`RegisterCrew`, `RegisterVendor` | Every admin (and the referring vendor for crew) | Bell + Push | High | `/approvals/people` |
| `ACCOUNT_APPROVED` | Their account is let in | Admin or vendor approves the registration<br>`ApproveUser` | The new user | Bell + Push + Email + WhatsApp | High | `/dashboard` |
| `ACCOUNT_REJECTED` | Their account is turned down, with a reason | Admin or vendor rejects the registration<br>`RejectUser` | The applicant | Bell + Push + Email | High | `/profile` |
| `ACCOUNT_INVITED` | Someone is invited to create an account | **DORMANT** -- nothing raises it yet | The invitee | Bell + Push + Email + WhatsApp | High | `/login` |
| `PROFILE_COMPLETED` | A crew member finishes filling in their profile and documents | **DORMANT** -- nothing raises it yet | Whoever manages them | Bell + Email | Low | `/profile` |
| `PASSWORD_RESET_OTP` | Password reset code | Deliberately NOT sent through this platform | n/a -- sent inline, never queued | Email + WhatsApp, inline | Critical | -- |

## 2. Vendors being given work

| Notification | Scenario | Triggered by | Who is told | Channels | Priority | Opens |
|---|---|---|---|---|---|---|
| `VENDOR_EVENT_INVITED` | A vendor is put on an event and asked to staff it | Admin creates an event / adds a shift with a vendor, assigns crew, or re-invites<br>`AddEventShift`, `AssignCrew`, `CreateEvent`, `ReinviteVendor` | The vendor | Bell + Push + Email + WhatsApp | Normal | `/my-events` |
| `VENDOR_ACCEPTED_EVENT` | A vendor takes the job | Vendor accepts the event invite<br>`VendorRespondToInvite` | Whoever invited them | Bell + Push | Normal | `/events` |
| `VENDOR_REJECTED_EVENT` | A vendor turns the job down -- the event now needs re-staffing | Vendor declines the event invite<br>`VendorRespondToInvite` | Whoever invited them | Bell + Push | High | `/events` |
| `VENDOR_INVITE_REVOKED` | A vendor is taken off an event | Admin revokes the vendor invite<br>`RevokeVendorInvite` | The vendor | Bell + Push + Email | Normal | `/my-events` |
| `VENDOR_EVENT_REMINDER` | Nudge a vendor before their event | **DORMANT** -- nothing raises it yet | The vendor | Bell + Push + WhatsApp | Normal | `/my-events` |

## 3. Crew being staffed -- the two-stage approval chain

| Notification | Scenario | Triggered by | Who is told | Channels | Priority | Opens |
|---|---|---|---|---|---|---|
| `CREW_INVITATION` | A crew member is invited to work a shift by their vendor | Vendor assigns a crew member or a whole group<br>`VendorAssignCrew`, `VendorAssignGroup` | The crew member | Bell + Push + WhatsApp | Normal | `/my-assignments` |
| `CREW_ASSIGNMENT` | A crew member is placed on a shift by an admin | Admin assigns crew to a shift<br>`AssignCrew` | The crew member | Bell + Push + WhatsApp | Normal | `/my-assignments` |
| `CREW_ACCEPTED_ASSIGNMENT` | Crew says yes -- the seat is filled | Crew accepts their invitation<br>`RespondAssignment` | Their vendor | Bell + Push | Normal | `/vendor-assignments` |
| `CREW_DECLINED_ASSIGNMENT` | Crew says no -- the seat needs refilling | Crew declines their invitation<br>`RespondAssignment` | Their vendor | Bell + Push | High | `/vendor-assignments` |
| `ASSIGNMENT_PENDING_APPROVAL` | A staffed crew member is waiting on a manager's decision | Vendor approves or directly forwards their crew<br>`VendorDirectForward`, `VendorReviewAssignment` | Every manager/admin | Bell + Push | High | `/manager-approvals` |
| `CREW_ASSIGNMENT_APPROVED` | Confirmed for the shift -- this is the one that means 'you are working' | Manager gives final approval<br>`ManagerReviewAssignment` | The crew member | Bell + Push + WhatsApp | Normal | `/my-assignments` |
| `CREW_ASSIGNMENT_REJECTED` | Not working this shift after all, with a reason | Vendor OR manager rejects the assignment<br>`ManagerReviewAssignment`, `VendorReviewAssignment` | The crew member | Bell + Push + WhatsApp | Normal | `/my-assignments` |
| `CREW_INVITE_REVOKED` | Pulled off a shift they were invited to | Vendor revokes the crew invite<br>`VendorRevokeCrewInvite` | The crew member | Bell + Push + WhatsApp | Normal | `/my-assignments` |
| `CREW_ASSIGNMENT_REMINDER` | Nudge crew before their shift | **DORMANT** -- nothing raises it yet | The crew member | Bell + Push + WhatsApp | High | `/my-assignments` |

## 4. The event itself changing

| Notification | Scenario | Triggered by | Who is told | Channels | Priority | Opens |
|---|---|---|---|---|---|---|
| `EVENT_ANNOUNCEMENT` | A free-text message from the organiser to everyone on an event | Admin sends an event announcement<br>`SendEventAnnouncement` | All crew and vendors on the event | Bell + Push + Email + WhatsApp | Normal | `/notifications` |
| `EVENT_CANCELLED` | The event is off -- nobody should travel | Admin changes event status to Cancelled<br>`ChangeEventStatus` | All crew and vendors on the event | Bell + Push + Email + WhatsApp | Critical | `/notifications` |
| `EVENT_UPDATED` | Event details changed (time, venue, brief) | **DORMANT** -- nothing raises it yet | All crew and vendors on the event | Bell + Push + WhatsApp | High | `/notifications` |
| `EVENT_STARTING` | The event is about to start | **DORMANT** -- nothing raises it yet | All crew and vendors on the event | Bell + Push + WhatsApp | High | `/notifications` |
| `SHIFT_CHANGED` | A shift's time or role changed under someone already assigned | **DORMANT** -- nothing raises it yet | Crew on that shift | Bell + Push + WhatsApp | High | `/my-assignments` |

## 5. Attendance on the day

| Notification | Scenario | Triggered by | Who is told | Channels | Priority | Opens |
|---|---|---|---|---|---|---|
| `ATTENDANCE_REMINDER` | Reminder to check in | **DORMANT** -- nothing raises it yet | The crew member | Bell + Push + WhatsApp | High | `/my-attendance` |
| `CHECK_IN_VERIFIED` | Their check-in was accepted | Vendor verifies a crew check-in / scans the QR<br>`VerifyCheckIn` | The crew member | Bell | Low | `/my-attendance` |

## 6. Money

| Notification | Scenario | Triggered by | Who is told | Channels | Priority | Opens |
|---|---|---|---|---|---|---|
| `PAYMENT_APPROVED` | A payment is approved and on its way | Admin approves a payment<br>`UpdatePaymentStatus` | The payee | Bell + Push + WhatsApp | Normal | `/my-payments` |
| `PAYMENT_REJECTED` | A payment claim is rejected, with a reason | Admin rejects a payment<br>`UpdatePaymentStatus` | The payee | Bell + Push + WhatsApp | Normal | `/my-payments` |
| `PAYROLL_RELEASED` | Money released -- the news people actually wait for | Admin marks a payment paid / releases a payroll batch<br>`UpdatePaymentStatus`, `UpdatePayrollStatus` | The payee (crew or vendor) | Bell + Push + Email + WhatsApp | Normal | `/my-payments` |

## The dormant ones

These have a template, a policy entry, a deep link and a push payload. They are
complete except for the one line that raises them, so each is a small piece of work
rather than a feature:

- **ACCOUNT_INVITED** -- Someone is invited to create an account. Needs: a trigger.
- **PROFILE_COMPLETED** -- A crew member finishes filling in their profile and documents. Needs: a trigger.
- **VENDOR_EVENT_REMINDER** -- Nudge a vendor before their event. Needs: a trigger.
- **CREW_ASSIGNMENT_REMINDER** -- Nudge crew before their shift. Needs: a trigger.
- **EVENT_UPDATED** -- Event details changed (time, venue, brief). Needs: a trigger.
- **EVENT_STARTING** -- The event is about to start. Needs: a trigger.
- **SHIFT_CHANGED** -- A shift's time or role changed under someone already assigned. Needs: a trigger.
- **ATTENDANCE_REMINDER** -- Reminder to check in. Needs: a trigger.

The four reminders (`CREW_ASSIGNMENT_REMINDER`, `ATTENDANCE_REMINDER`,
`VENDOR_EVENT_REMINDER`, `EVENT_STARTING`) all need the same thing that does not exist
yet: a scheduled job that looks ahead at shifts. `EVENT_UPDATED` and `SHIFT_CHANGED`
need the edit commands to notice a material change and say so -- worth doing, because
a venue or time change reaching nobody is the same failure as a cancellation reaching
nobody.

## Deliberate silences

Not everything should notify, and these are choices rather than gaps:

- **Payment and payroll CREATION** -- rows land Pending or Draft. The real news is
  approval, rejection or release, so creating one tells nobody.
- **The vendor approval stage** -- a vendor approving crew only forwards them to a
  manager. Telling the crew member at that point would be telling them they are
  confirmed when they are not.
- **CHECK_IN_VERIFIED is bell-only** -- a receipt for something the person did on that
  same phone seconds earlier. Pushing it back at them adds nothing.
- **PROFILE_COMPLETED is never pushed** -- low-value housekeeping. Interrupting a
  phone for it is how people learn to swipe our notifications away unread.
- **The reset OTP never enters the outbox** -- the outbox is durable, so queueing it
  would write the plaintext code into the database and leave it there after use.
  It also has no push template on purpose: a lock-screen preview is readable by
  whoever is holding the phone.

## One notification, several channels, no duplicates

Each notification is ONE row with a delivery row per channel, keyed by a
BusinessEventKey, so a worker retry or a double-click cannot produce a second
message. Invite-style keys deliberately carry a timestamp, because re-inviting
somebody resurrects the same assignment row in place and a key built from the row id
alone would make the second invitation look like a duplicate and drop it -- the
person would never learn they were wanted again.


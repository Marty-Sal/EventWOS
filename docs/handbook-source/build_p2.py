# ============================================================
#   COVER
# ============================================================
story.append(Spacer(1, 4.5*cm))
story.append(Paragraph('EventWOS', S['cover_title']))
story.append(Spacer(1, 6))
story.append(Paragraph('Operations Handbook', S['cover_title']))
story.append(Spacer(1, 24))
story.append(Paragraph('2026 . Volume 1', S['cover_sub']))
story.append(Spacer(1, 90))
story.append(Paragraph('THE PLAYBOOK FOR EVENT MANAGERS,<br/>VENDORS AND CREW', S['cover_tag']))
story.append(Spacer(1, 40))
story.append(Paragraph('Prepared by the EventWOS team . Confidential', S['cover_ver']))
story.append(PageBreak())

# ============================================================
#   CONTENTS
# ============================================================
set_chapter(0, 'Contents')
p('Contents', 'h1')
hr(color=EMERALD, thickness=1.5)
gap(10)

toc = [
    ('01', 'About EventWOS',           'What the platform is . who uses it . the four portals',                            '05'),
    ('02', "Who\'s Who — The Roles",   'Admin . Manager . Vendor . Crew — what each one owns',                             '07'),
    ('03', 'How Work Flows',           'From event creation to payment, in one diagram',                                    '09'),
    ('04', 'Phases of an Event',       'Before . Day-Of . During . After',                                                  '11'),
    ('05', 'Manager Quick Guide',      'Setup, requisites, key-start checklist, team briefing',                             '13'),
    ('06', 'Vendor Quick Guide',       'Receiving allocations, assigning crew, day-of duties',                              '16'),
    ('07', 'Crew Quick Guide',         'Getting invited, setup password, checking in, getting paid',                        '18'),
    ('08', 'System Walkthrough',       "Every key screen, in order — Dashboard to Payments",                                '20'),
    ('09', 'Approvals &amp; Modifications', 'Approval queue . reprints . cancellations . re-assigns',                       '25'),
    ('10', 'Attendance &amp; Check-In',     'QR handshake . geofence . edge cases',                                         '27'),
    ('11', 'Payments &amp; Payroll',        'Payroll batches . per-crew rate . reconciliation',                             '29'),
    ('12', 'Reports &amp; Audit',           'What to check, how often, and where to find it',                               '31'),
    ('13', 'Troubleshooting',               'The situations you will actually run into',                                    '33'),
    ('14', 'Handover &amp; After-Event',    'Closing the loop — the ritual before you sign off',                            '35'),
]

toc_data = []
for num, title, sub, pg in toc:
    toc_data.append([
        Paragraph('<font color="#059669"><b>{}</b></font>'.format(num), S['toc_num']),
        [Paragraph('<b>{}</b>'.format(title), S['toc_row']),
         Paragraph(sub, S['toc_sub'])],
        Paragraph('p. {}'.format(pg), S['toc_pg']),
    ])
tbl = Table(toc_data, colWidths=[1.4*cm, CONTENT_W - 3.2*cm, 1.8*cm])
tbl.setStyle(TableStyle([
    ('VALIGN', (0,0), (-1,-1), 'TOP'),
    ('BOTTOMPADDING', (0,0), (-1,-1), 10),
    ('TOPPADDING', (0,0), (-1,-1), 6),
    ('LINEBELOW', (0,0), (-1,-2), 0.4, SLATE_100),
    ('LEFTPADDING', (0,0), (-1,-1), 0),
    ('RIGHTPADDING', (0,0), (-1,-1), 0),
]))
story.append(tbl)
story.append(PageBreak())

# ============================================================
#   CHAPTER 01 — About
# ============================================================
set_chapter(1, 'About EventWOS')
story.append(Spacer(1, 5*cm))
story.append(Paragraph('01', S['chapter_num']))
story.append(Spacer(1, 4))
story.append(Paragraph('About EventWOS', S['chapter_title']))
story.append(Spacer(1, 8))
story.append(Paragraph('What the platform is, who it serves, and why it exists.', S['chapter_tag']))
story.append(PageBreak())

p('About EventWOS', 'h1')
hr(color=EMERALD, thickness=1.5)
gap(6)
p("EventWOS is an end-to-end platform for running large live events — concerts, festivals, sports, "
  "conferences, private functions. It replaces the WhatsApp-groups-and-spreadsheets way of "
  "coordinating an event with a single system that everyone on the ground shares. Managers plan "
  "the event and the shifts; vendors supply crew against those shifts; crew members show up, "
  "check in with a QR handshake, and get paid — and the manager sees the whole thing in "
  "real time.")

p('One workflow, four portals', 'h2')
p("Every organization that runs an event has the same four archetypes on the ground. EventWOS "
  "gives each of them a portal tuned to what they need — and only what they need — so no one is "
  "buried under screens meant for someone else.")

gap(4)
story.append(RolePersona([
    ('A', 'Admin',   'Platform-level control — creates managers, sets roles, sees audit trails.', NAVY),
    ('M', 'Manager', 'Owns events — plans shifts, allocates to vendors, approves crew, runs payroll.', INDIGO),
    ('V', 'Vendor',  'Manpower supplier — receives allocations, assigns their crew to shifts.', EMERALD),
    ('C', 'Crew',    'Person on the ground — checks in, marks attendance, receives payment.', AMBER),
]))
gap(10)

p('What EventWOS replaces', 'h2')
bullets([
    "<b>Spreadsheets</b> for crew rosters — replaced by a live database with roles, sessions, and audit trail.",
    "<b>WhatsApp groups</b> for shift coordination — replaced by real-time SignalR notifications.",
    "<b>Paper attendance sheets</b> — replaced by QR-verified check-in with geofence enforcement.",
    "<b>End-of-event payroll chaos</b> — replaced by payroll batches auto-computed from attendance.",
    "<b>&quot;Who approved this?&quot;</b> after the fact — replaced by an immutable audit log of every sensitive action.",
])

gap(6)
story.append(Callout(
    'A note on trust',
    'EventWOS is built for events where money changes hands and access matters. Every action '
    'that touches identity, access, or payment is logged, approved, and reversible only through '
    'the platform. That is what makes it different from a group chat and a shared spreadsheet.',
    color=EMERALD, tint=EMERALD_SFT
))
story.append(PageBreak())

# ============================================================
#   CHAPTER 02 — Roles
# ============================================================
set_chapter(2, "Who\'s Who")
story.append(Spacer(1, 5*cm))
story.append(Paragraph('02', S['chapter_num']))
story.append(Paragraph("Who\'s Who", S['chapter_title']))
story.append(Spacer(1, 8))
story.append(Paragraph('The four roles that make an event happen — and what each one owns.', S['chapter_tag']))
story.append(PageBreak())

p("Who\'s Who — The Roles", 'h1')
hr(color=EMERALD, thickness=1.5)
gap(8)
p("Every role in EventWOS has a portal, a permission set, and a clear scope of responsibility. "
  "This chapter is the reference for who does what. When something goes wrong or a decision needs "
  "to be made, come back here to figure out whose desk it lands on.")

p('Admin', 'h3')
story.append(KeyValueTable([
    ('Portal',        'Admin portal — full platform reach, all events, all users.'),
    ('Owns',          'Managers, roles, permissions, platform-wide settings.'),
    ('Can do',        'Create/deactivate managers . assign role templates . view audit log across the platform . manage the Scope of Work catalog.'),
    ('Cannot do',     "Approve crew for a specific event (that\'s the manager\'s job) or check crew in (that\'s the event supervisor\'s device)."),
    ('When to escalate', "Only when a manager\'s account itself needs changing, or when the audit trail is disputed."),
]))
gap(10)

p('Event Manager', 'h3')
story.append(KeyValueTable([
    ('Portal',        'Manager portal — the busiest surface in the app.'),
    ('Owns',          "Events end-to-end. From &quot;we\'re doing a show on the 12th&quot; to &quot;vendor invoices are settled&quot;."),
    ('Can do',        'Create events . define shifts . allocate crew counts to vendors . approve or reject crew . watch attendance live . create payroll batches . rate vendors after the show.'),
    ('Cannot do',     "Assign an individual crew member to a shift (that\'s the vendor\'s responsibility once allocation is approved)."),
    ('When to escalate', 'When a vendor is chronically failing, when a payroll batch needs to be reversed, or when an event needs to be cancelled after publish.'),
]))
gap(10)

p('Vendor', 'h3')
story.append(KeyValueTable([
    ('Portal',        'Vendor portal — narrow but critical.'),
    ('Owns',          "The people. Once a manager gives them &quot;5 F&amp;B for Saturday&quot;, filling those five slots is on them."),
    ('Can do',        'Accept an allocation . pick crew from their roster . assign to shifts . watch which of their crew showed up . see their payment status.'),
    ('Cannot do',     'Approve their own crew for an event, or change the crew count a manager gave them. If they need more, they ask.'),
    ('When to escalate', 'When a crew member no-shows and they need a replacement approved, or when an allocation number is wrong.'),
]))
gap(10)

p('Crew', 'h3')
story.append(KeyValueTable([
    ('Portal',        'Crew portal — deliberately minimal, phone-first.'),
    ('Owns',          'Themselves. Showing up, checking in, checking out.'),
    ('Can do',        "See what they\'re assigned to . check in with the supervisor\'s QR . check out . see their payments."),
    ('Cannot do',     'Move themselves between shifts, invite others, or dispute a payment inside the app. Those go through their vendor or the manager.'),
    ('When to escalate', 'Payment mismatch, wrong shift assigned, or check-in failing on the day.'),
]))
story.append(PageBreak())

# ============================================================
#   CHAPTER 03 — How Work Flows
# ============================================================
set_chapter(3, 'How Work Flows')
story.append(Spacer(1, 5*cm))
story.append(Paragraph('03', S['chapter_num']))
story.append(Paragraph('How Work Flows', S['chapter_title']))
story.append(Spacer(1, 8))
story.append(Paragraph("The path a single event takes, from &quot;let\'s do this&quot; to &quot;invoice settled&quot;.", S['chapter_tag']))
story.append(PageBreak())

p('How Work Flows', 'h1')
hr(color=EMERALD, thickness=1.5)
gap(6)
p("Every event that runs through EventWOS follows the same eight-step arc. The names of the "
  "steps map exactly to the tabs and buttons in the app — if you can find yourself on this "
  "diagram, you can find yourself in the software.")

gap(8)
story.append(FlowChart([
    ('1 . Manager creates the event',                INDIGO),
    ('2 . Manager defines shifts &amp; scopes of work', INDIGO),
    ('3 . Manager allocates crew counts to vendors', EMERALD),
    ('4 . Vendors assign specific crew to shifts',   EMERALD),
    ('5 . Manager approves the assignments',         NAVY),
    ('6 . Day of event — QR check-in / check-out',   AMBER),
    ('7 . Manager creates payroll batch',            NAVY),
    ('8 . Vendors and crew see settled payments',    EMERALD),
]))
gap(10)

p('The two golden rules', 'h2')
p("If you remember only two things about the flow above, remember these — they are the invariants "
  "the system defends. Everything else can be argued about; these cannot.")
gap(4)

story.append(Callout(
    'Rule 1 — Nobody works without an approved assignment',
    "A crew member cannot check in unless (a) a manager has approved their assignment, and "
    "(b) the shift they\'re trying to check into is theirs. This is why the manager\'s "
    "approval queue is the most important tab in the app.",
    color=INDIGO, tint=INDIGO_SOFT
))
gap(6)
story.append(Callout(
    'Rule 2 — Attendance is the source of truth for payment',
    "Payroll batches compute the line total as <b>per-crew rate x attended</b>. If somebody was "
    "assigned but did not check in, they are not on the payroll. This is why the check-in flow is "
    "sacred: it directly determines who gets paid.",
    color=EMERALD, tint=EMERALD_SFT
))
story.append(PageBreak())

# ============================================================
#   CHAPTER 04 — Phases
# ============================================================
set_chapter(4, 'Phases of an Event')
story.append(Spacer(1, 5*cm))
story.append(Paragraph('04', S['chapter_num']))
story.append(Paragraph('Phases of an Event', S['chapter_title']))
story.append(Spacer(1, 8))
story.append(Paragraph('What happens before, during and after — and who does what in each phase.', S['chapter_tag']))
story.append(PageBreak())

p('Phases of an Event', 'h1')
hr(color=EMERALD, thickness=1.5)
gap(6)
p("Borrowed straight from the way we already work — but named and checklisted so nothing "
  "quietly slips off the plate. Read this like a runbook: you are always in one of these four "
  "phases, and each has an owner and an exit criterion.")

story.append(ChecklistBox('Phase 1 — Before the Event (Planning)', [
    'Event created in the system with venue, start, end and expected crew size.',
    'Shifts defined — each with a Scope of Work (Box Office, F&amp;B, Gates, etc.) and a crew count.',
    'Vendor allocations issued — each vendor knows their number for each shift.',
    'Vendors have accepted and their crew is assigned in the system.',
    'Manager has approved every crew member — the approval queue is empty.',
    'Every crew member has received their assignment on their phone.',
], color=INDIGO, tint=INDIGO_SOFT))
gap(8)

story.append(ChecklistBox('Phase 2 — Day-Of Setup', [
    'Supervisor is on-site with a working phone and the QR scanner ready.',
    'Geofence is verified — the site coordinates match the event address.',
    'A test check-in has been performed (one crew in, then reversed).',
    'Any last-minute reassignments are approved.',
    "The Live indicator in the manager\'s sidebar is green (real-time is connected).",
], color=AMBER, tint=AMBER_SFT))
gap(8)

story.append(ChecklistBox('Phase 3 — During the Event', [
    'Crew check in as they arrive — supervisor scans QR, geofence confirms location.',
    'Attendance dashboard is watched — any shift under quota gets a call.',
    'Late arrivals and reassignments are handled in real time.',
    'Manager keeps an eye on the audit log for anything unexpected.',
    'Every check-in triggers a real-time update — no one has to refresh.',
], color=EMERALD, tint=EMERALD_SFT))
gap(8)

story.append(ChecklistBox('Phase 4 — After the Event', [
    'Any remaining check-outs are recorded before closing the shift.',
    'Manager creates a payroll batch from the completed event.',
    'Per-crew rate is entered for each vendor and direct-crew line.',
    'Batch is submitted — every crew member sees their expected payment.',
    "Vendor ratings are captured while it\'s all fresh.",
    'Event is marked Completed — no further edits allowed.',
], color=ROSE, tint=ROSE_SFT))
story.append(PageBreak())

# ============================================================
#   CHAPTER 05 — Manager Quick Guide
# ============================================================
set_chapter(5, 'Manager Quick Guide')
story.append(Spacer(1, 5*cm))
story.append(Paragraph('05', S['chapter_num']))
story.append(Paragraph('Manager Quick Guide', S['chapter_title']))
story.append(Spacer(1, 8))
story.append(Paragraph('The two-page briefing every event manager should read before their first show.', S['chapter_tag']))
story.append(PageBreak())

p('Manager Quick Guide', 'h1')
hr(color=EMERALD, thickness=1.5)
gap(6)
p("If you are the event manager, this is the chapter you keep printed on your desk. Everything "
  "you need to do sits inside the Manager portal at "
  "<font name='Courier'>eventwos.app/manager</font>. You log in with your mobile number and an OTP.")

p('The five tabs you will live in', 'h3')
story.append(KeyValueTable([
    ('Dashboard',   'Real-time pulse of every event you own. Live check-in counts, upcoming events, alerts.'),
    ('Events',      "Create events, edit them, define shifts, and see who\'s allocated to what."),
    ('Approvals',   "The queue of vendor-assigned crew waiting for your signoff. Empty this before the event."),
    ('Attendance',  'Live view during the event . logs . summary by event afterwards.'),
    ('Payments',    'Create payroll batches from completed events. This is where money moves.'),
]))
gap(10)

p('Requisites before your first event', 'h3')
bullets([
    'A working smartphone with a modern browser (Chrome, Safari, Edge — last 2 versions).',
    'A laptop for anything longer than a five-minute task — the manager desktop UI is far denser than the phone.',
    'Location permission granted in the browser (for check-in oversight and geofence sanity).',
    'Notifications enabled — so real-time alerts about missed check-ins actually reach you.',
    'Your admin has added you as a Manager and shared your mobile-verified login.',
])

gap(6)
p('Key-start checklist — before you announce the event', 'h3')
story.append(ChecklistBox('Ready to publish', [
    'Event has a name, venue, start time and end time — no placeholders.',
    'Shifts are defined and their crew counts add up to the total you actually need.',
    'Every Scope of Work referenced by a shift exists in the catalog (Admin > Scopes).',
    'Vendors you plan to allocate to are already onboarded and Active.',
    "The event\'s geofence radius is set (default 300m is fine for most venues; tighter for indoor).",
    'You have a supervisor identified for the day-of QR scanning.',
], color=INDIGO, tint=INDIGO_SOFT))
story.append(PageBreak())

p('Briefing your on-ground team', 'h2')
p("On the day of the event, the person doing the QR check-in isn\'t you — it\'s a supervisor "
  "(often the vendor lead). Ten minutes with them the day before saves an hour of chaos the "
  "day of. Cover these points.")

gap(4)
bullets([
    "How the QR handshake works — supervisor shows the QR, crew scans; both sides confirm.",
    "What to do if geofence rejects a legitimate check-in (contact you; you can override from the manager portal).",
    "Late arrivals — check them in when they arrive; don\'t backdate.",
    "Check-out at end of shift is not optional — that\'s what pins &quot;actually worked&quot; for payroll.",
    "Never let someone else\'s phone check in on a crew member\'s behalf. It defeats the geofence.",
])

gap(8)
p('Common day-of mistakes', 'h3')
story.append(KeyValueTable([
    ("&quot;My live indicator is red&quot;",
     "Real-time connection dropped. Refresh the page. If it stays red, the crew can still work — the audit trail records everything — but you won\'t see updates in real time until it\'s back."),
    ('&quot;A vendor is short one person&quot;',
     'Vendor should mark the no-show in their portal . you approve a replacement . new person can check in immediately.'),
    ("&quot;The check-in count doesn\'t match the head count on the floor&quot;",
     "Two possibilities: someone forgot to check in (fix from the manager portal — Attendance > Log manual check-in), or someone is on-site without an approved assignment (a security issue — investigate)."),
    ('&quot;A crew member says they were paid the wrong amount&quot;',
     'Never fix it in a chat message. Open the payroll batch . verify per-crew rate and attended count . issue an amendment batch if needed. All changes are audit-logged.'),
]))
story.append(PageBreak())

# ============================================================
#   CHAPTER 06 — Vendor Quick Guide
# ============================================================
set_chapter(6, 'Vendor Quick Guide')
story.append(Spacer(1, 5*cm))
story.append(Paragraph('06', S['chapter_num']))
story.append(Paragraph('Vendor Quick Guide', S['chapter_title']))
story.append(Spacer(1, 8))
story.append(Paragraph('For manpower suppliers — accepting allocations, assigning crew, day-of duties.', S['chapter_tag']))
story.append(PageBreak())

p('Vendor Quick Guide', 'h1')
hr(color=EMERALD, thickness=1.5)
gap(6)
p("You are a manpower vendor. A manager has onboarded you into EventWOS, given you a login, and "
  "will start sending you crew allocations for their events. Here is exactly what happens on your "
  "side, and what you need to do to be a vendor managers actually want to work with.")

p('The lifecycle of an allocation', 'h2')
story.append(FlowChart([
    ('You receive a notification: 5 F&amp;B, Saturday, DOME',           EMERALD),
    ('You open Vendor Assignments and see the allocation',              EMERALD),
    ('You pick crew from your roster and assign them to the shift',     INDIGO),
    ('The manager approves them (you can see it happen live)',          NAVY),
    ('Your crew get their assignment on their phones',                  AMBER),
    ('On the day: they check in; you see it live',                      EMERALD),
    ('After the event: manager creates payroll batch, you get paid',    NAVY),
]))
story.append(PageBreak())

p('Your daily discipline', 'h2')
p("Managers pick vendors partly on capability, but mostly on reliability. Three habits will keep "
  "you at the top of their list.")

gap(4)
bullets([
    "<b>Assign early.</b> The moment an allocation lands, fill it. Don\'t wait until the day before — managers will start second-guessing you.",
    "<b>Keep your roster clean.</b> Deactivate crew who leave; update mobile numbers when they change. A stale roster is why assignments fail.",
    "<b>Communicate no-shows early.</b> If someone tells you they can\'t make it, mark it in your portal immediately. The manager can approve a replacement in minutes.",
])

gap(10)
p('What the manager sees about you', 'h3')
story.append(KeyValueTable([
    ('Rating',              'A star rating a manager gives you after every completed event. Averages over time — visible to other managers.'),
    ('Fill rate',           'How often your allocated seats got assigned crew before the event started.'),
    ('Show-up rate',        'Of the crew you assigned, how many actually checked in.'),
    ('Last-minute drops',   'How often you replaced a crew member less than 24 hours before the shift.'),
]))
gap(10)

story.append(Callout(
    'The one number that matters',
    "Show-up rate is what managers watch most closely. A vendor with 100% fill rate but 60% "
    "show-up rate is worse than a vendor with 80% fill rate and 100% show-up rate. If you "
    "assign someone, make sure they show up.",
    color=EMERALD, tint=EMERALD_SFT
))
story.append(PageBreak())

# ============================================================
#   CHAPTER 07 — Crew Quick Guide
# ============================================================
set_chapter(7, 'Crew Quick Guide')
story.append(Spacer(1, 5*cm))
story.append(Paragraph('07', S['chapter_num']))
story.append(Paragraph('Crew Quick Guide', S['chapter_title']))
story.append(Spacer(1, 8))
story.append(Paragraph("For the person on the ground — the shortest chapter in the book, deliberately.", S['chapter_tag']))
story.append(PageBreak())

p('Crew Quick Guide', 'h1')
hr(color=EMERALD, thickness=1.5)
gap(6)
p("You are a crew member. Your job is to show up, do your work, and get paid — the app should "
  "get out of your way. Here is everything you need to know.")

p('1 . You get an invite', 'h3')
p("A vendor or a manager creates your profile. You get a text with a link. Open it on your "
  "phone, set a password, done. Save "
  "<font name='Courier'>eventwos.app/crew</font> as a bookmark.")

p('2 . You see your assignments', 'h3')
p("Open the app. The first screen shows every shift you\'re assigned to — venue, date, time. "
  "Tap into any of them for the full details, including who your supervisor is.")

p('3 . On the day — check in', 'h3')
p("When you arrive at the venue, find the supervisor. They show you a QR code from their phone. "
  "You scan it with EventWOS. If your location matches the venue (the geofence check), you\'re "
  "checked in. Green tick, done. You can also do it the other way around — you show a QR, they scan it.")

p('4 . At the end of the shift — check out', 'h3')
p("Same handshake. The supervisor scans you (or you scan them), the app marks you out. "
  "This step is what tells the system you actually worked the shift — <b>your payment depends "
  "on it</b>. Don\'t leave without checking out.")

p('5 . You get paid', 'h3')
p("After the event ends, the manager creates a payroll batch. Your payment shows up in the app. "
  "Payment is a promise to pay — the actual money follows however your vendor or manager has "
  "arranged it (bank transfer, cash, UPI). The app just records the amount so nothing gets forgotten.")

gap(8)
story.append(Callout(
    'When something is wrong',
    "Wrong shift? Wrong payment? Can\'t check in? Don\'t argue with anyone on-site — call your "
    "vendor or your supervisor. Everything you see is a record; someone with access can fix it "
    "from the manager portal in seconds.",
    color=AMBER, tint=AMBER_SFT
))
story.append(PageBreak())

# ============================================================
#   CHAPTER 08 — System Walkthrough
# ============================================================
set_chapter(8, 'System Walkthrough')
story.append(Spacer(1, 5*cm))
story.append(Paragraph('08', S['chapter_num']))
story.append(Paragraph('System Walkthrough', S['chapter_title']))
story.append(Spacer(1, 8))
story.append(Paragraph("Every key screen, in the order you\'ll use them.", S['chapter_tag']))
story.append(PageBreak())

p('System Walkthrough', 'h1')
hr(color=EMERALD, thickness=1.5)
gap(6)
p("This chapter mirrors the shape of the manager portal. Each screen has a headline job — if "
  "you know what a screen is <i>for</i>, the buttons on it explain themselves.")

p('Dashboard', 'h3')
p("The home page after login. It answers one question: <b>what needs my attention right now?</b> "
  "Upcoming events, pending approvals, live check-in counts, and any alerts. Everything here "
  "is a shortcut to a deeper screen — never a place to sit and stare at.")

p('Events', 'h3')
p("The catalog of every event you\'ve ever created — filterable, searchable, with a status pill "
  "(Draft . Published . Live . Completed . Cancelled). Click any event to open its <b>Detail</b> "
  "view, where you\'ll spend most of your planning time.")

p('Event Detail', 'h3')
p("The heart of the app. From here you can add/edit shifts, allocate to vendors, watch the "
  "approval count fill up, and — during the event — see check-ins land in real time. Every "
  "change here is audit-logged.")

story.append(PageBreak())

p('Approval Queue', 'h3')
p("The queue of every crew assignment waiting for your signoff. Sorted oldest-first, because "
  "the crew member on the phone is <i>waiting</i>. Approve or reject each one — approved crew "
  "get an immediate notification with their assignment details.")

gap(4)
story.append(Callout(
    'Empty the queue by end-of-day',
    "A crew member who doesn\'t know they\'re confirmed is a no-show risk. Set aside 10 minutes "
    "a day to keep this queue empty. If it takes longer than that, your vendors are assigning "
    "too late — talk to them.",
    color=INDIGO, tint=INDIGO_SOFT
))
gap(6)

p('Vendors', 'h3')
p("The vendor directory. Add, deactivate, and rate vendors. The rating column is what tells you "
  "which vendors managers with more experience trust — always sort by it before allocating to "
  "someone new.")

p('Scope of Work', 'h3')
p("The catalog of shift categories: Box Office, F&amp;B, Gates, Accreditation, Security, and so on. "
  "This is a shared catalog — new categories should be added sparingly. If you find yourself "
  "adding a new scope for every event, you probably want a note field on the shift instead.")

p('Attendance', 'h3')
p("Three tabs: <b>Scan Check-In</b> (the supervisor view), <b>Logs</b> (immutable history of "
  "every check-in and check-out), and <b>Summary by Event</b> (the number that goes into payroll). "
  "The Logs tab is your friend when someone disputes their hours.")

story.append(PageBreak())

p('Payments &amp; Payroll', 'h3')
p("Where completed events turn into money owed. Create a payroll batch, pick the event, enter "
  "per-crew rates, and the app computes the line totals from attended counts. Once submitted, "
  "the batch is a record — amendments require a new batch, not an edit.")

p('Audit Log', 'h3')
p("Every sensitive action in the system, in one place, immutable. Logins, approvals, rejections, "
  "role changes, session revocations, OTP failures. When something looks off, this is the first "
  "screen to open.")

p('Sessions', 'h3')
p("Every device currently signed into your account (or any user\'s, if you\'re an admin). "
  "Revoke a session with one click — the device is signed out instantly.")

p('Roles &amp; Permissions', 'h3')
p("The admin\'s workspace. Create role templates (e.g. &quot;Regional Manager&quot; with access to a "
  "subset of the app), then assign users to them. Fine-grained permissions live on the Permissions "
  "page — grant them sparingly, because every permission is a way to make a mistake at scale.")
story.append(PageBreak())

# ============================================================
#   CHAPTER 09 — Approvals & Modifications
# ============================================================
set_chapter(9, 'Approvals &amp; Modifications')
story.append(Spacer(1, 5*cm))
story.append(Paragraph('09', S['chapter_num']))
story.append(Paragraph('Approvals &amp; Modifications', S['chapter_title']))
story.append(Spacer(1, 8))
story.append(Paragraph("The manager\'s core loop — approve, reject, adjust.", S['chapter_tag']))
story.append(PageBreak())

p('Approvals &amp; Modifications', 'h1')
hr(color=EMERALD, thickness=1.5)
gap(6)
p("The core loop of managing an event is <b>Vendor assigns > Manager approves</b>. Everything else "
  "is variations on that theme. This chapter covers the queue itself and every modification action "
  "you have available after approval.")

p('Approving an assignment', 'h2')
bullets([
    "Open <b>Approvals</b>. The queue is sorted oldest-first.",
    "Click a row to see the crew member\'s profile, their photo, and which shift they\'re proposed for.",
    "Verify the crew is legitimate (photo, name, mobile) and appropriate for the shift (skills, scope of work).",
    "Approve > crew is confirmed and notified. Reject > vendor is notified and can assign someone else.",
    "If details are wrong but the person is right, use <b>Send back</b> to ask the vendor to fix and resubmit.",
])

gap(6)
p('Modifications you can make after approval', 'h2')
story.append(KeyValueTable([
    ('Move a crew member to a different shift',
     'From the Event Detail, drag or use the &quot;Reassign&quot; action on the crew row. Only allowed before the shift starts.'),
    ('Cancel an approved assignment',
     "Approved crew member can\'t make it? Cancel the assignment. The seat re-opens for the vendor to fill. Audit-logged."),
    ('Bulk-cancel a shift',
     'If a shift is cut from the event, archiving it will cascade-cancel every assignment on it. Prompted with a confirmation.'),
    ("Change a shift\'s crew count",
     'You can grow a shift freely (adds capacity). Shrinking it is only allowed if fewer people are currently assigned than the new number.'),
    ('Un-approve (send back)',
     "If you approved someone by mistake, you can undo it as long as they haven\'t checked in yet. After check-in, the assignment is locked."),
]))
story.append(PageBreak())

p('The rules the system enforces', 'h2')
p("EventWOS won\'t let you do certain things — not out of pedantry, but because they corrupt the "
  "audit trail or the payment math. Knowing these ahead of time saves you five minutes of "
  "arguing with the &quot;action not allowed&quot; error.")

gap(4)
bullets([
    "<b>You can\'t shrink a shift below its currently-assigned count.</b> Cancel assignments first, then shrink.",
    "<b>You can\'t edit an event after it\'s Completed or Cancelled.</b> These are terminal states — the record is closed.",
    "<b>You can\'t un-approve someone who has already checked in.</b> Their work is a fact; only their payment can be adjusted.",
    "<b>You can\'t delete an assignment — only cancel it.</b> Deletion would break the audit trail. Cancel is the correct verb.",
    "<b>Payroll batches can\'t be edited after submit.</b> Wrong rate? New batch, marked as an amendment. All changes stay visible.",
])
gap(10)

story.append(Callout(
    'When you think the system is wrong',
    "Nine times out of ten, the error message is protecting you from a mistake you\'d regret. "
    "The tenth time — read the message carefully, then check the Audit Log. If it\'s genuinely "
    "wrong, take a screenshot and escalate to platform support. Do not work around it.",
    color=ROSE, tint=ROSE_SFT
))
story.append(PageBreak())

# ============================================================
#   CHAPTER 10 — Attendance
# ============================================================
set_chapter(10, 'Attendance &amp; Check-In')
story.append(Spacer(1, 5*cm))
story.append(Paragraph('10', S['chapter_num']))
story.append(Paragraph('Attendance &amp; Check-In', S['chapter_title']))
story.append(Spacer(1, 8))
story.append(Paragraph("The QR handshake, the geofence, and every edge case they don\'t cover.", S['chapter_tag']))
story.append(PageBreak())

p('Attendance &amp; Check-In', 'h1')
hr(color=EMERALD, thickness=1.5)
gap(6)
p("Attendance is the source of truth for payment. Everything about how it works is designed to "
  "protect that truth — and to make fraud (checking in from home, checking in on someone else\'s "
  "behalf, back-dating hours) impossible without an audit trail.")

p('The QR handshake', 'h2')
p("A check-in is a two-party act. One party is the crew member, the other is the supervisor. "
  "One of them shows a QR code from their phone, the other scans it. Either direction works — "
  "the system doesn\'t care who scans whom, only that both are present at the same time in the "
  "same place.")

gap(4)
story.append(FlowChart([
    ("Supervisor generates a QR on their phone",           INDIGO),
    ("Crew scans it (or supervisor scans crew\'s QR)",     INDIGO),
    ('Both devices submit their location',                 AMBER),
    ('System verifies both are inside the geofence',       AMBER),
    ('Check-in recorded . both devices confirm',           EMERALD),
]))
gap(10)

p('The geofence', 'h3')
p("Every event has a location (lat/lng from the venue address) and a radius (default 300m, "
  "manager-configurable). Both parties must be inside that circle when they scan. This is what "
  "stops someone from checking in remotely, and it\'s why the manager\'s job before the event "
  "includes verifying the venue coordinates.")
story.append(PageBreak())

p('Edge cases', 'h2')
story.append(KeyValueTable([
    ('&quot;My check-in is failing — location denied.&quot;',
     "The crew member hasn\'t granted browser location permission. Fix on-device: browser settings > allow location for eventwos.app. Retry."),
    ('&quot;Both allowed location, but check-in still fails.&quot;',
     "One of them isn\'t inside the geofence. Common cause: venue GPS drift or an inaccurate radius. Manager can widen the geofence or override the check-in."),
    ('&quot;A crew member arrived but forgot to check in.&quot;',
     "Supervisor can still QR them in when discovered. If the shift is over, the manager can log a manual check-in from Attendance > Logs; it\'s marked as manager-logged and audit-logged."),
    ("&quot;A crew member checked in but didn\'t check out.&quot;",
     "The shift ends open. Payroll treats them as attended for the shift regardless — the check-out is a discipline metric, not a payment gate. Follow up with them for future reliability."),
    ('&quot;Two people scanned each other by accident.&quot;',
     "Cross-scans are ignored — the system requires one supervisor role and one crew role. If two crew scan each other, nothing happens."),
    ('&quot;A supervisor lost their phone mid-event.&quot;',
     "Any manager with attendance permission can act as the supervisor from their own device. The QR is not tied to a specific device — it\'s tied to a role."),
]))
story.append(PageBreak())

# ============================================================
#   CHAPTER 11 — Payments
# ============================================================
set_chapter(11, 'Payments &amp; Payroll')
story.append(Spacer(1, 5*cm))
story.append(Paragraph('11', S['chapter_num']))
story.append(Paragraph('Payments &amp; Payroll', S['chapter_title']))
story.append(Spacer(1, 8))
story.append(Paragraph('How attendance becomes money owed, and how that money is reconciled.', S['chapter_tag']))
story.append(PageBreak())

p('Payments &amp; Payroll', 'h1')
hr(color=EMERALD, thickness=1.5)
gap(6)
p("EventWOS doesn\'t move money — banks do. But it is the ledger. Every peso, dollar or rupee "
  "owed after an event lives here, and this chapter is how you make sure it lives here correctly.")

p('The payroll batch', 'h2')
p("Payroll is done in <b>batches</b>, one per completed event. A batch is a snapshot: at the "
  "moment you submit it, it captures who was on the payroll, at what rate, for what count of "
  "attended shifts. Once submitted, it\'s frozen.")

gap(4)
story.append(FlowChart([
    ('Pick a completed event',                                  INDIGO),
    ('For each vendor / direct-crew line, enter per-crew rate', INDIGO),
    ('System computes line total = rate x attended',            EMERALD),
    ('Review roster and totals . submit batch',                 NAVY),
    ('Every crew member sees their expected payment',           EMERALD),
]))
gap(10)

p('What &quot;attended&quot; means for payroll', 'h3')
p("A crew member is <b>attended</b> for a shift if they have a check-in record. Check-out is "
  "not required. This is deliberate: forgetting to check out is a discipline issue, not a "
  "reason to withhold pay. If you want to gate pay on check-out, that\'s a separate business "
  "policy — talk to platform support before layering it on.")
story.append(PageBreak())

p('Amendments', 'h2')
p("Frozen batches can\'t be edited. What you do instead is create an <b>amendment batch</b> — "
  "same event, only the corrected lines. Amendments are visible in the payment history "
  "alongside the original, so nothing is hidden.")

gap(6)
p('Common payment situations', 'h3')
story.append(KeyValueTable([
    ('Wrong per-crew rate submitted',
     "Create an amendment batch with the delta (positive or negative). Never delete the original — it\'s a record."),
    ('Crew member disputes attended count',
     'Open Attendance > Logs for the event, filter to that crew member, and screenshot the check-in trail. Fix the batch if genuinely wrong, otherwise show them the log.'),
    ("Vendor asks for a summary of what they\'re owed",
     'Vendor portal has a Payment Statement per event. It shows every line, every rate, every total. Print or export.'),
    ('One vendor covers multiple scopes at different rates',
     'Split into separate batch lines by scope. The batch supports multiple lines per vendor for exactly this.'),
    ('Payment made outside the app — need to close the loop',
     'Mark the batch line as Settled with a note (bank ref, UPI transaction ID, or &quot;cash 12/07&quot;). This closes the ledger for that line.'),
]))
gap(10)
story.append(Callout(
    'Reconcile weekly, not monthly',
    "Payroll batches are cheap to create and easy to reconcile. Do it after every event, not once "
    "a month. The person who paid the crew last Saturday remembers what happened; the person "
    "trying to reconcile three weeks of events at month-end does not.",
    color=EMERALD, tint=EMERALD_SFT
))
story.append(PageBreak())

# ============================================================
#   CHAPTER 12 — Reports & Audit
# ============================================================
set_chapter(12, 'Reports &amp; Audit')
story.append(Spacer(1, 5*cm))
story.append(Paragraph('12', S['chapter_num']))
story.append(Paragraph('Reports &amp; Audit', S['chapter_title']))
story.append(Spacer(1, 8))
story.append(Paragraph('What to check, how often, and where to find it.', S['chapter_tag']))
story.append(PageBreak())

p('Reports &amp; Audit', 'h1')
hr(color=EMERALD, thickness=1.5)
gap(6)
p("Reports are how you know the system is healthy before someone tells you it isn\'t. This "
  "chapter is the maintenance schedule.")

p('The four reports you should actually look at', 'h2')
story.append(KeyValueTable([
    ('Event Summary — Attendance',
     'Per event: assigned vs. attended vs. no-show. Read after every event. If no-show rate exceeds 10%, investigate the vendor.'),
    ('Vendor Performance',
     'Fill rate, show-up rate, average rating. Read monthly. Use to reshape your vendor bench.'),
    ('Payroll Reconciliation',
     'Every batch, every line, every settled/unsettled status. Read weekly. Nothing older than 14 days should still be unsettled.'),
    ('Audit Log',
     'Every sensitive action. Read on demand — when something looks off, or when compliance asks.'),
]))
gap(10)

p('The audit log — read it like a story', 'h2')
p("The audit log is the most powerful tool in the platform and the most under-used. Every "
  "sensitive action — login, logout, role change, approval, session revocation, OTP failure — "
  "appears there with an actor, a timestamp, and an entity reference.")

gap(4)
bullets([
    "Sort by newest first when investigating a live incident.",
    "Sort by oldest first when auditing a specific user\'s history.",
    "Filter by actor name to reconstruct what someone did in a session.",
    "Filter by entity ID to see everything that ever happened to a specific event, user or shift.",
    "The Actor column shows real names, not just IDs — even for login events (which historically showed &quot;System&quot;).",
])
story.append(PageBreak())

# ============================================================
#   CHAPTER 13 — Troubleshooting
# ============================================================
set_chapter(13, 'Troubleshooting')
story.append(Spacer(1, 5*cm))
story.append(Paragraph('13', S['chapter_num']))
story.append(Paragraph('Troubleshooting', S['chapter_title']))
story.append(Spacer(1, 8))
story.append(Paragraph('The situations you will actually run into — and what to do first.', S['chapter_tag']))
story.append(PageBreak())

p('Troubleshooting', 'h1')
hr(color=EMERALD, thickness=1.5)
gap(6)
p("Everything below has happened at least once. The pattern is always the same: check the "
  "obvious thing first, then check the audit log, then escalate.")

story.append(KeyValueTable([
    ('The Live indicator is red.',
     '<b>First:</b> refresh the page. <b>Then:</b> check your network — SignalR needs a persistent connection. <b>Finally:</b> the platform status page (link in Admin). The event data is unaffected; only real-time updates are down.'),
    ('&quot;Session expired&quot; message on every page.',
     'Refresh tokens are healthy but your access token expired. Log out and back in. If it persists, someone else revoked your session (check Sessions).'),
    ("A crew member says they didn\'t receive their invite text.",
     'Verify the mobile number in their profile (typos are the #1 cause). Resend from the Users screen. If OTPs consistently fail, the mobile number is likely blocked by the carrier — try a different number.'),
    ('QR check-in loops without confirming.',
     "One party\'s location isn\'t being reported. Kill the browser tab, reopen, and grant location again. If persistent, use a different phone as a fallback — the QR is not tied to a specific device."),
    ('Payroll batch line total looks wrong.',
     '<b>First:</b> confirm the attended count matches Attendance > Summary. <b>Then:</b> confirm the per-crew rate you entered. Almost always one of those two — not a system bug.'),
    ('Event card shows &quot;13/21 Crew Approved&quot; but shifts total 22.',
     'Historical drift bug — fixed as of July 2026. If seen on an event created before the fix, an amendment is auto-applied on next boot. If it persists after that, take a screenshot and escalate.'),
    ('&quot;Reconnecting...&quot; badge stuck for &gt; 30 seconds.',
     "Same as red Live indicator. Refresh. If a whole team of users sees it, it\'s platform-wide — check the status page before flooding support."),
    ('Mobile view is missing columns from a table.',
     "Scroll the table horizontally — as of the latest release every table scrolls sideways on narrow screens. If you can\'t scroll, force-refresh (Ctrl+Shift+R) to bust the cache."),
]))
story.append(PageBreak())

# ============================================================
#   CHAPTER 14 — Handover
# ============================================================
set_chapter(14, 'Handover &amp; After-Event')
story.append(Spacer(1, 5*cm))
story.append(Paragraph('14', S['chapter_num']))
story.append(Paragraph('Handover &amp; After-Event', S['chapter_title']))
story.append(Spacer(1, 8))
story.append(Paragraph('The ritual before you sign off. Do it, or regret it in three weeks.', S['chapter_tag']))
story.append(PageBreak())

p('Handover &amp; After-Event', 'h1')
hr(color=EMERALD, thickness=1.5)
gap(6)
p("The show is over. Everybody wants to go home. But an event isn\'t closed until this "
  "checklist is closed. Skipping it is how you get a message three weeks later from a crew "
  "member who says they weren\'t paid — and you have no way to prove otherwise.")

story.append(ChecklistBox('Before you leave the venue', [
    'Every crew member who worked has a check-in record (spot-check Attendance > Logs).',
    'Every open check-in has a corresponding check-out (or is noted as a forgotten check-out).',
    'The supervisor has confirmed head count matches system count.',
    'Any last-minute reassignments are approved (not sitting in the queue).',
], color=AMBER, tint=AMBER_SFT))
gap(8)

story.append(ChecklistBox('Within 24 hours after the event', [
    'The event is marked Completed (Events > this event > status change).',
    'The payroll batch is created with per-crew rates.',
    'The batch is submitted — every crew member can see their expected payment.',
    'Vendor ratings are captured for every vendor who worked.',
    "The event\'s post-mortem note is added (what went well, what didn\'t).",
], color=EMERALD, tint=EMERALD_SFT))
gap(8)

story.append(ChecklistBox('Within one week after the event', [
    'Every batch line is either Settled or has a note explaining why not.',
    'Vendor performance stats have been reviewed — any vendor with a red flag is on your radar.',
    'The audit log has been spot-checked for anything unexpected.',
    "Feedback from the on-ground team has been captured (in the event\'s notes, not in a chat).",
], color=INDIGO, tint=INDIGO_SOFT))

gap(14)
story.append(Callout(
    'The last word',
    "Nothing in EventWOS matters more than the trust of the people who use it. Every check-in "
    "is someone\'s day of work. Every payroll line is somebody\'s livelihood. Treat the app "
    "accordingly, and it will treat you accordingly.",
    color=EMERALD, tint=EMERALD_SFT
))
story.append(PageBreak())

# ============================================================
#   BACK COVER
# ============================================================
story.append(Spacer(1, 6*cm))
story.append(Paragraph('EventWOS', S['cover_title']))
story.append(Spacer(1, 30))
story.append(Paragraph('Thank you.', S['cover_sub']))
story.append(Spacer(1, 60))
story.append(Paragraph('Written for the people who make live events happen.', S['cover_ver']))
story.append(Spacer(1, 8))
story.append(Paragraph('Version 1.0 . 2026', S['cover_ver']))

# ============================================================
#   DOC BUILDER + TEMPLATE SWITCHING
# ============================================================
class HandbookDoc(BaseDocTemplate):
    def __init__(self, filename, **kw):
        BaseDocTemplate.__init__(self, filename, pagesize=A4,
                                 leftMargin=MARGIN_X, rightMargin=MARGIN_X,
                                 topMargin=MARGIN_TOP, bottomMargin=MARGIN_BOT, **kw)
        cover_frame = Frame(0, 0, PAGE_W, PAGE_H, id='cover',
                            leftPadding=MARGIN_X, rightPadding=MARGIN_X,
                            topPadding=MARGIN_TOP, bottomPadding=MARGIN_BOT)
        chapter_frame = Frame(0, 0, PAGE_W, PAGE_H, id='chapter',
                              leftPadding=MARGIN_X + 0.8*cm, rightPadding=MARGIN_X,
                              topPadding=MARGIN_TOP, bottomPadding=MARGIN_BOT)
        body_frame = Frame(MARGIN_X, MARGIN_BOT + 0.4*cm, CONTENT_W,
                           PAGE_H - MARGIN_TOP - MARGIN_BOT - 0.4*cm, id='body')
        self.addPageTemplates([
            PageTemplate(id='Cover',   frames=cover_frame,   onPage=draw_cover_bg),
            PageTemplate(id='Chapter', frames=chapter_frame, onPage=draw_chapter_bg),
            PageTemplate(id='Body',    frames=body_frame,    onPage=draw_body_chrome),
        ])

def compile_final_story(src):
    """
    Interleave NextPageTemplate flowables. Key ReportLab fact:
    NextPageTemplate takes effect on the *next* PageBreak — meaning
    it needs to be placed BEFORE the PageBreak whose resulting page
    should use the new template.

    Layout we want, per page:
        Page 1     : Cover template (via initial NextPageTemplate)
        Page 2-3   : Body template  (TOC)
        Page 4     : Chapter template (Ch 1 opener) — dark navy full-bleed
        Page 5..   : Body template  (Ch 1 body)
        ... repeat for each chapter ...
        Last page  : Cover template (back cover)
    """
    out = []
    # Initial template for page 1
    out.append(NextPageTemplate('Cover'))

    i = 0
    n = len(src)
    seen_first_break = False

    while i < n:
        el = src[i]

        # First PageBreak in the whole story = the one that ends the cover.
        # The NEXT page after it should use Body → emit NextPageTemplate('Body')
        # BEFORE the PageBreak.
        if not seen_first_break and isinstance(el, PageBreak):
            out.append(NextPageTemplate('Body'))
            out.append(el)
            seen_first_break = True
            i += 1
            continue

        # Every SetChapterFlowable(num >= 1) means: the CURRENT last PageBreak in
        # `out` must be preceded by NextPageTemplate('Chapter') so that the page
        # about to be produced (which contains the opener content) uses Chapter.
        # Then, before the PageBreak that ends the opener page, we need to
        # switch back to Body.
        if isinstance(el, SetChapterFlowable):
            if el.num == 0:
                # Contents chapter — already on Body from the first PageBreak
                out.append(el)
                i += 1
                continue

            # Find the last PageBreak in `out` and insert NextPageTemplate('Chapter')
            # right BEFORE it so the resulting new page uses Chapter template.
            for k in range(len(out) - 1, -1, -1):
                if isinstance(out[k], PageBreak):
                    out.insert(k, NextPageTemplate('Chapter'))
                    break

            # Now emit the SetChapter + opener content until the closing PageBreak
            out.append(el)
            i += 1
            while i < n and not isinstance(src[i], PageBreak):
                out.append(src[i])
                i += 1

            # About to emit the PageBreak that closes the opener — the NEXT page
            # is body content, so switch back to Body BEFORE this PageBreak.
            out.append(NextPageTemplate('Body'))
            if i < n:
                out.append(src[i])
                i += 1
            continue

        out.append(el)
        i += 1

    # Back cover: the LAST PageBreak in `out` produces the final page (back cover).
    # Insert NextPageTemplate('Cover') before that last PageBreak.
    for k in range(len(out) - 1, -1, -1):
        if isinstance(out[k], PageBreak):
            out.insert(k, NextPageTemplate('Cover'))
            break
    return out

final_story = compile_final_story(story)

import os
out_path = '/tmp/handbook/EventWOS_Handbook_2026.pdf'
doc = HandbookDoc(out_path)
doc.build(final_story)

sz = os.path.getsize(out_path)
print('OK  Built {}  ({:.1f} KB)'.format(out_path, sz/1024))

# Scouting Regional Game Submission Automation
## Software Design & Requirements Specification (SDD)

Version: 1.0
Status: Draft

# 1. Executive Summary

This document defines the complete specification for a system that automates the intake, storage, review, and reporting of assignment submissions for a regional Scouting event.

Participating groups submit photos, videos, and other media into dedicated Slack channels. The application automates:

- Submission processing
- Assignment identification
- File storage
- Review workflow
- Statistics and reporting
- Dashboard visualization
- Development infrastructure using .NET Aspire

---

# 2. Business Context

## Current Process

Review staff manually:

1. Monitor Slack channels.
2. Download all uploaded files.
3. Determine assignment numbers.
4. Move files into the correct folder.
5. Move files again after review.
6. Track statistics manually.

## Problems

- Time consuming.
- Human errors.
- Duplicate work.
- Difficult progress tracking.
- No centralized reporting.

---

# 3. Objectives

## Primary Objectives

- Eliminate manual downloads.
- Eliminate manual file organization.
- Automate review workflows.
- Provide real-time visibility.
- Provide configuration-driven setup.

## Secondary Objectives

- Event statistics.
- Leaderboards.
- Export capabilities.
- Future AI features.

---

# 4. Stakeholders

## Administrators

Configure event settings.

## Reviewers

Approve, reject, or mark submissions.

## Event Organization

View progress and statistics.

## Developers

Maintain and deploy the application.

---

# 5. Functional Requirements

## FR-001 Slack Monitoring

The system shall monitor configured Slack channels.

Acceptance Criteria:

- Detect new messages.
- Detect file uploads.
- Ignore unknown channels.
- Process events in near real time.

## FR-002 Assignment Number Extraction

The system shall extract assignment numbers from Slack messages.

Acceptance Criteria:

- Parse message text.
- Validate format.
- Log failures.
- Flag unresolved submissions.

## FR-003 Group Configuration

Groups shall be configurable through appsettings.json.

Acceptance Criteria:

- Add groups.
- Rename groups.
- Modify channel mappings.
- No code changes required.

## FR-004 File Download Processing

The system shall download eligible attachments.

Supported Files:

- jpg
- jpeg
- png
- gif
- mp4
- mov
- pdf
- future supported formats

## FR-005 Folder Structure

```text
/group-name/        (new, unreviewed files)
  /approved/
  /rejected/
  /awesome/
```

## FR-006 Submission Registration

The system shall create a submission record.

Metadata:

- SubmissionId
- AssignmentNumber
- SlackChannelId
- SlackMessageId
- Submitter
- Filename
- Timestamp
- Status

## FR-007 Configurable Review Workflow

Review status shall be driven by configurable reaction mappings.

Example:

```json
{
  "ReviewStatusReactions": {
    "Approved": "thumbsup",
    "Rejected": "x",
    "Awesome": "heart"
  }
}
```

## FR-008 File Movement

The system shall automatically move files.

Mappings:

- Pending -> Approved
- Pending -> Rejected
- Pending -> Awesome

## FR-009 Dashboard

Dashboard shall display:

### Overview

- Total submissions
- Pending
- Approved
- Rejected
- Awesome

### Group Statistics

- Submissions per group
- Approval rate
- Rejection rate
- Awesome count

### Assignment Statistics

- Assignment completion counts
- Status breakdowns

## FR-010 Search & Filtering

Support:

- By group
- By assignment
- By status
- By reviewer
- By date range

## FR-011 Submission Detail View

Display:

- Slack message
- Uploaded files
- Assignment number
- Group
- Review state
- Reviewer
- Timestamps

## FR-012 Configuration Management

All event-specific settings shall be configurable.

Settings:

- Groups
- Channels
- Emoji mappings
- Storage root
- Logging

---

# 6. User Stories

## Administrator

As an administrator,
I want files automatically organized,
So that I do not manually process Slack uploads.

## Reviewer

As a reviewer,
I want to react with an emoji,
So that files are automatically categorized.

## Organizer

As an organizer,
I want real-time statistics,
So that I can track event progress.

---

# 7. Non-Functional Requirements

## Performance

- Upload processing within 30 seconds.
- Reaction processing within 30 seconds.

## Reliability

- No file loss.
- Retry failed jobs.
- Idempotent processing.

## Security

- Environment variable secrets.
- Authentication/Authorization for this POC not needed.

## Maintainability

- Configuration driven.
- Modular architecture.

## Developer Productivity

- Local setup under 10 minutes.
- Single command startup.

---

# 8. Architectural Decision Record

## ADR-001 Mandatory Technology Stack

Required:

- ASP.NET Core (.NET 9+)
- C#
- Blazor Server
- Entity Framework Core
- PostgreSQL
- .NET Aspire
- OpenTelemetry

---

# 9. Architecture Requirements

## AR-001 Aspire Orchestration

Use Aspire AppHost.

## AR-002 Database Provisioning

Use Aspire-managed PostgreSQL.

## AR-003 Service Discovery

Use Aspire references and injected connection strings.

## AR-004 Observability

Use:

- Logs
- Metrics
- Distributed tracing
- Health checks

## AR-005 Local Startup

```bash
dotnet run --project ScoutingAutomation.AppHost
```

---

# 10. Solution Structure

```text
ScoutingAutomation.sln

/src
├── ScoutingAutomation.AppHost
├── ScoutingAutomation.ServiceDefaults
├── ScoutingAutomation.Web
├── ScoutingAutomation.Infrastructure
├── ScoutingAutomation.Domain
└── ScoutingAutomation.Contracts
```

---

# 11. Database Design

## Groups

- Id
- Name
- SlackChannelId

## Assignments

- Id
- Number
- StandardPoints
- AwesomePoints

## Submissions

- Id
- GroupId
- AssignmentNumber
- FileName
- Status
- CreatedAt

## Reviews

- Id
- SubmissionId
- Reviewer
- Status
- CreatedAt

---

# 12. Integration Design

## Slack

Capabilities:

- Message events
- Reaction events
- File downloads

## Storage

Supported:

- Local file system
- Network share
- OneDrive
- SharePoint

---

# 13. State Machine

Submission States:

```text
Received
  ↓
Pending
  ├─> Approved
  ├─> Rejected
  └─> Awesome
```

---

# 14. Logging Strategy

Log:

- Startup
- Configuration validation
- Downloads
- File moves
- Review actions
- Errors
- Retries

---

# 15. Security Model

- Secrets stored outside source control.
- Principle of least privilege.
- Encrypted transport.
- Audit logging.

---

# 16. Testing Strategy

## Unit Tests

- Assignment parser
- Workflow logic
- Status transitions

## Integration Tests

- Slack integration
- Database integration
- Storage integration

## End-To-End

- Upload file
- Review file
- Verify movement
- Verify dashboard statistics

---

# 17. Deployment

Development:

- Aspire
- PostgreSQL container

Production:

- Linux container hosting
- Managed PostgreSQL

---

# 18. MVP Scope

Included:

- Slack processing
- File storage
- Reviews
- Dashboard
- Statistics

Excluded:

- OCR
- AI classification

---

# 19. Definition of Done

The project is complete when:

1. Files are downloaded automatically.
2. Files are stored automatically.
3. Emoji reviews trigger file movement.
4. Statistics are available in the dashboard.
5. Configuration requires no code changes.
6. The environment starts through .NET Aspire.
7. Automated tests pass.

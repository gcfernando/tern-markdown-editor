namespace Tern.App.Help;

public sealed record StarterTemplate(string Name, string Description, string Markdown);

/// <summary>One catalog for welcome and replay. Creating a note normalizes it to CRLF.</summary>
public static class StarterTemplates
{
    public static IReadOnlyList<StarterTemplate> All { get; } =
    [
        new("Meeting Notes", "Capture decisions, owners and next steps.", "# Meeting notes\n\nDate: YYYY-MM-DD\n\n## Attendees\n\n- \n\n## Agenda\n\n- \n\n## Decisions\n\n- \n\n## Action items\n\n- [ ] Task — owner, due date\n"),
        new("Project Plan", "Define goals, milestones, tasks and risks.", "# Project plan\n\n## Goal\n\n## Scope\n\n## Milestones\n\n| Milestone | Owner | Target date |\n| --- | --- | --- |\n| First deliverable | | |\n\n## Tasks\n\n- [ ] Define the first deliverable\n\n## Risks and next steps\n"),
        new("Task Checklist", "Track tasks and completion.", "# Task checklist\n\n- [ ] First task\n- [ ] Second task\n  - [ ] Subtask\n\n## Notes\n"),
        new("Daily Journal", "Reflect on your day and tomorrow’s priorities.", "# Daily journal\n\nDate: YYYY-MM-DD\n\n## Today’s priorities\n\n- [ ] \n\n## Notes and reflections\n\n## What went well\n\n## Tomorrow\n"),
        new("README", "Explain a project and how to use it.", "# Project name\n\nDescribe the project.\n\n## Requirements\n\n## Getting started\n\n## Usage\n\n## Contributing\n\n## License\n"),
        new("Architecture Decision Record", "Record a decision and why it was made.", "# ADR-001: Decision title\n\nStatus: Proposed\nDate: YYYY-MM-DD\n\n## Context\n\nWhat problem needs a decision?\n\n## Decision\n\nWhat approach will we take?\n\n## Consequences\n\nBenefits, costs, risks and alternatives.\n"),
        new("Technical Design", "Describe the problem, approach and rollout.", "# Technical design\n\nStatus: Draft\nOwner: \n\n## Problem and goals\n\n## Non-goals\n\n## Proposed design\n\n## Interfaces and data\n\n## Alternatives\n\n## Risks and security\n\n## Testing and rollout\n"),
        new("Incident Postmortem", "Capture impact, causes and preventive actions.", "# Incident postmortem\n\nIncident date: YYYY-MM-DD\nOwner: \n\n## Summary and impact\n\n## Timeline\n\n| Time | Event |\n| --- | --- |\n| | |\n\n## Contributing factors\n\n## What went well\n\n## Follow-up actions\n\n- [ ] Action — owner, due date\n"),
        new("Release Notes", "Communicate changes and known limitations.", "# Release notes — version\n\nRelease date: YYYY-MM-DD\n\n## Added\n\n## Changed\n\n## Fixed\n\n## Known limitations\n\n## Upgrade instructions\n"),
        new("Project Status Report", "Share progress, risks and requests.", "# Project status report\n\nPeriod: \nOwner: \nStatus: On track / At risk / Blocked\n\n## Highlights\n\n## Progress against milestones\n\n## Risks and blockers\n\n## Decisions needed\n\n## Next steps\n\n- [ ] Action — owner, due date\n")
    ];
}

namespace Tern.App.Help;

public sealed record StarterTemplate(string Name, string Description, string Markdown, string? SourcePath = null, string? FileName = null);

/// <summary>
/// One catalog for welcome and replay. Creating a note normalizes it to CRLF. Placeholders use [square brackets] because
/// angle brackets would be treated as HTML and disappear from the preview. Formats follow the source named on each template.
/// </summary>
public static partial class StarterTemplates
{
    public static IReadOnlyList<StarterTemplate> All { get; } =
    [
        new("Meeting Notes", "Capture decisions, owners and next steps.", MeetingNotes),
        new("Project Plan", "Define goals, scope, milestones, owners and risks.", ProjectPlan),
        new("Task Checklist", "Track work with GitHub-style task lists.", TaskChecklist),
        new("Daily Journal", "Plan your day and reflect on it.", DailyJournal),
        new("README", "GitHub's recommended contents: what, why, how to start, help and maintainers.", Readme, FileName: "README.md"),
        new("Architecture Decision Record", "MADR format: context, options, decision and consequences.", DecisionRecord),
        new("Technical Design", "Describe the problem, options, design and rollout.", TechnicalDesign),
        new("Incident Postmortem", "Google SRE format: impact, root causes, action items, lessons and timeline.", Postmortem),
        new("Changelog", "Keep a Changelog format with Semantic Versioning.", Changelog, FileName: "CHANGELOG.md"),
        new("Project Status Report", "Share progress, risks and the decisions you need.", StatusReport),
        .. AIProjectTemplates()
    ];

    private const string MeetingNotes = """
        # Meeting notes: [topic]

        Date: YYYY-MM-DD
        Time: [start] to [end]
        Location or link: [room or meeting link]
        Facilitator: [name]
        Note taker: [name]

        ## Attendees

        - [Name, role]

        ## Goal

        [What this meeting must achieve.]

        ## Agenda

        1. [Topic] ([owner], [minutes])

        ## Discussion

        ### [Topic]

        - [Key point]

        ## Decisions

        | Decision | Decided by | Reason |
        | --- | --- | --- |
        | [What was decided] | [Name] | [Why] |

        ## Action items

        - [ ] [Action] (owner: [name], due: YYYY-MM-DD)

        ## Next meeting

        Date: YYYY-MM-DD
        """;

    private const string ProjectPlan = """
        # Project plan: [project name]

        Owner: [name]
        Sponsor: [name]
        Last updated: YYYY-MM-DD
        Status: Not started

        ## Goal

        [The outcome this project delivers and why it matters.]

        ## Success measures

        - [Measurable result, for example: reduce response time to under 2 seconds]

        ## Scope

        ### In scope

        - [Included work]

        ### Out of scope

        - [Excluded work]

        ## Milestones

        | Milestone | Owner | Due date | Status |
        | --- | --- | --- | --- |
        | [Milestone] | [Name] | YYYY-MM-DD | Not started |

        ## Tasks

        - [ ] [Task] (owner: [name], due: YYYY-MM-DD)

        ## Risks

        | Risk | Likelihood | Impact | Response | Owner |
        | --- | --- | --- | --- | --- |
        | [What could go wrong] | Medium | High | [How to reduce it] | [Name] |

        ## Dependencies

        - [Team, system or decision this project needs]

        ## Stakeholders

        | Name | Role | Needs to know |
        | --- | --- | --- |
        | [Name] | [Role] | [What and how often] |
        """;

    private const string TaskChecklist = """
        # Task checklist: [name]

        Due: YYYY-MM-DD

        ## To do

        - [ ] [First task]
        - [ ] [Second task]
          - [ ] [Subtask]

        ## In progress

        - [ ] [Task being worked on]

        ## Done

        - [x] [Example of a finished task]

        ## Notes

        [Anything needed to finish these tasks.]
        """;

    private const string DailyJournal = """
        # Daily journal

        Date: YYYY-MM-DD

        ## Top priorities

        1. [Most important thing today]
        2. [Second priority]
        3. [Third priority]

        ## Schedule

        - [Time]: [Event or focus block]

        ## Notes

        [Ideas, events and things to remember.]

        ## What went well

        - [Win]

        ## What to improve

        - [Lesson]

        ## Tomorrow

        - [ ] [First thing to do]
        """;

    private const string Readme = """
        # [Project name]

        [One or two sentences: what this project does and who it is for.]

        ## Why use it

        - [The main problem it solves]
        - [What makes it useful]

        ## Getting started

        ### Requirements

        - [Runtime, tool or account, with version]

        ### Install

        ```sh
        [install command]
        ```

        ### Run

        ```sh
        [run command]
        ```

        ## Usage

        ```sh
        [the most common example]
        ```

        ## Getting help

        - Documentation: [link]
        - Questions and bugs: [link to issues]

        ## Contributing

        See [CONTRIBUTING.md](CONTRIBUTING.md).

        ## Maintainers

        - [Name or team]

        ## License

        [License name]. See [LICENSE](LICENSE).
        """;

    private const string DecisionRecord = """
        # [Short title of the problem and chosen solution]

        Status: Proposed
        Date: YYYY-MM-DD
        Decision-makers: [names]
        Consulted: [people whose opinion was sought]
        Informed: [people kept up to date]

        ## Context and Problem Statement

        [Describe the situation and the problem in two or three sentences, or as a question.]

        ## Decision Drivers

        - [Force or concern, for example: cost, security or delivery date]

        ## Considered Options

        - [Option 1]
        - [Option 2]
        - [Option 3]

        ## Decision Outcome

        Chosen option: "[Option 1]", because [reason it best meets the decision drivers].

        ### Consequences

        - Good, because [positive result]
        - Bad, because [negative result or accepted trade-off]

        ### Confirmation

        [How you will check that the decision was implemented, for example a review or test.]

        ## Pros and Cons of the Options

        ### [Option 1]

        - Good, because [argument]
        - Bad, because [argument]

        ### [Option 2]

        - Good, because [argument]
        - Bad, because [argument]

        ### [Option 3]

        - Good, because [argument]
        - Bad, because [argument]

        ## More Information

        [Links to related decisions, issues or evidence.]
        """;

    private const string TechnicalDesign = """
        # Technical design: [feature or system]

        Status: Draft
        Author: [name]
        Reviewers: [names]
        Last updated: YYYY-MM-DD

        ## Summary

        [What is being built, in three sentences or fewer.]

        ## Problem and goals

        [The problem, who has it and why it matters now.]

        ### Goals

        - [Measurable goal]

        ### Non-goals

        - [What this design deliberately does not cover]

        ## Proposed design

        ### Overview

        [How the solution works.]

        ```mermaid
        flowchart LR
            A[Client] --> B[Service]
            B --> C[(Data store)]
        ```

        ### Data and interfaces

        [Data models, APIs and contracts that change.]

        ## Alternatives considered

        | Option | Pros | Cons | Why not chosen |
        | --- | --- | --- | --- |
        | [Option] | [Pros] | [Cons] | [Reason] |

        ## Security and privacy

        [Data handled, access control and threats.]

        ## Testing

        [How correctness and performance will be verified.]

        ## Rollout and rollback

        1. [Rollout step]

        Rollback: [how to undo the change safely]

        ## Open questions

        - [ ] [Question] (owner: [name])
        """;

    private const string Postmortem = """
        # Postmortem: [incident title]

        Date: YYYY-MM-DD
        Authors: [names]
        Status: Draft
        Severity: [level]

        This review is blameless. Describe systems and decisions, not personal fault.

        ## Summary

        [What happened, in one or two sentences.]

        ## Impact

        [Who and what was affected, for how long, and how much.]

        ## Root Causes

        [The underlying reasons this incident was possible.]

        ## Trigger

        [The specific event that started the incident.]

        ## Resolution

        [What restored service.]

        ## Detection

        [How the team found out, and how long that took.]

        ## Action Items

        | Action Item | Type | Owner | Bug |
        | --- | --- | --- | --- |
        | [Action] | Prevent | [Name] | [Link] |
        | [Action] | Mitigate | [Name] | [Link] |
        | [Action] | Detect | [Name] | [Link] |

        ## Lessons Learned

        ### What went well

        - [Item]

        ### What went wrong

        - [Item]

        ### Where we got lucky

        - [Item]

        ## Timeline

        All times in UTC.

        | Time | Event |
        | --- | --- |
        | YYYY-MM-DD HH:MM | [Event] |

        ## Supporting Information

        - [Dashboards, logs or related documents]
        """;

    private const string Changelog = """
        # Changelog

        All notable changes to this project will be documented in this file.

        The format is based on Keep a Changelog 1.1.0,
        and this project adheres to Semantic Versioning 2.0.0.

        ## [Unreleased]

        ### Added

        - [New feature]

        ## [1.0.0] - YYYY-MM-DD

        ### Added

        - [New feature]

        ### Changed

        - [Change to existing behavior]

        ### Deprecated

        - [Feature that will be removed later]

        ### Removed

        - [Feature removed in this release]

        ### Fixed

        - [Bug fix]

        ### Security

        - [Vulnerability fix]
        """;

    private const string StatusReport = """
        # Project status report: [project name]

        Reporting period: YYYY-MM-DD to YYYY-MM-DD
        Owner: [name]
        Overall status: On track

        Use one status: On track, At risk or Off track.

        ## Summary

        [Three sentences: where the project stands, what changed and what you need.]

        ## Progress

        | Area | Status | Notes |
        | --- | --- | --- |
        | Scope | On track | [Note] |
        | Schedule | On track | [Note] |
        | Budget | On track | [Note] |

        ## Completed this period

        - [Result]

        ## Planned next period

        - [ ] [Work] (owner: [name], due: YYYY-MM-DD)

        ## Risks and issues

        | Item | Impact | Response | Owner |
        | --- | --- | --- | --- |
        | [Risk or issue] | High | [Action] | [Name] |

        ## Decisions needed

        - [Decision, who must make it and by when]
        """;
}

namespace Tern.App.Help;

/// <summary>
/// AI-assisted development and project documentation templates, following agents.md, Claude Code memory,
/// Gemini CLI context files, the Agent Skills specification, arc42 and EARS requirement patterns.
/// </summary>
public static partial class StarterTemplates
{
    // A method, not a field: static initializer order across partial files is undefined.
    private static StarterTemplate[] AIProjectTemplates() =>
    [
        new("AGENTS.md", "Instructions for coding agents, in standard Markdown. Save at the repository root. In a monorepo, the nearest AGENTS.md wins.", Agents, FileName: "AGENTS.md"),
        new("CLAUDE.md", "Claude Code project instructions. Save as ./CLAUDE.md or ./.claude/CLAUDE.md and keep it under 200 lines.", Claude, FileName: "CLAUDE.md"),
        new("GEMINI.md", "Gemini CLI project context. Save at the project root or in a component folder. Check it with /memory show.", Gemini, FileName: "GEMINI.md"),
        new("SKILL.md: Code Review", "Agent skill for reviewing changes. Save as .claude/skills/code-review/SKILL.md. The folder name must match the name field.", CodeReviewSkill, FileName: "SKILL.md"),
        new("SKILL.md: Debugging", "Agent skill for finding root causes. Save as .claude/skills/debugging/SKILL.md. The folder name must match the name field.", DebuggingSkill, FileName: "SKILL.md"),
        new("SKILL.md: Testing", "Agent skill for writing and running tests. Save as .claude/skills/testing/SKILL.md. The folder name must match the name field.", TestingSkill, FileName: "SKILL.md"),
        new("ARCHITECTURE.md", "System architecture using the 12 arc42 sections.", Architecture, FileName: "ARCHITECTURE.md"),
        new("CONTEXT.md", "Business background, users, goals, constraints and terms for people and AI tools.", Context, FileName: "CONTEXT.md"),
        new("REQUIREMENTS.md", "Testable requirements written with EARS patterns.", Requirements, FileName: "REQUIREMENTS.md"),
    ];

    private const string Agents = """
        # AGENTS.md

        ## Project overview

        [What this project does, its main language and framework, and where the main code lives.]

        ## Setup commands

        - Install dependencies: `[command]`
        - Start the development server: `[command]`

        ## Build and test commands

        - Build: `[command]`
        - Run all tests: `[command]`
        - Run one test: `[command]`
        - Lint: `[command]`

        ## Code style

        - [Language rule, for example: TypeScript strict mode]
        - [Formatting rule, for example: single quotes, no semicolons]
        - [Naming or structure rule]

        ## Testing instructions

        - Add or update tests for every behavior change.
        - Run `[test command]` and `[lint command]` before finishing.
        - Fix all failures before committing.

        ## Security considerations

        - Never commit secrets, tokens or personal data.
        - [Project-specific rule, for example: validate all external input]

        ## Pull request instructions

        - Title format: `[area] Short description`
        - Run lint and tests before committing.
        - Explain what changed and why.
        """;

    private const string Claude = """
        # CLAUDE.md

        [One or two sentences: what this project is and who uses it.]

        ## Commands

        - Build: `[command]`
        - Test all: `[command]`
        - Test one: `[command]`
        - Lint: `[command]`

        ## Architecture

        - `[src/api/]`: [what lives here]
        - `[src/ui/]`: [what lives here]

        ## Code style

        - [Specific, checkable rule, for example: use 2-space indentation]
        - [Pattern to follow, with a file path as an example]

        ## Workflow

        - Run `[test command]` after changing code.
        - Keep changes focused on the request.
        - Ask before changing public APIs or database schemas.

        ## Important

        - Never commit secrets or edit `[generated folder]`.
        - [A project gotcha Claude could not work out from the code]

        ## More detail

        Import longer guides instead of copying them here:

        @README.md
        @docs/[guide].md
        """;

    private const string Gemini = """
        # Project: [project name]

        ## General Instructions

        - [What this project does and how Gemini should help.]
        - When you generate code, follow the existing style in the surrounding files.
        - Add or update tests for behavior changes.
        - Do not change unrelated code.

        ## Commands

        - Build: `[command]`
        - Test: `[command]`
        - Lint: `[command]`

        ## Coding Style

        - [Indentation and formatting rule]
        - [Naming rule]
        - [Language-specific rule]

        ## Project Structure

        - `[src/]`: [purpose]
        - `[tests/]`: [purpose]

        ## Constraints

        - Never commit secrets or personal data.
        - [Project-specific rule]

        ## Imported Context

        Import component instructions with a relative path:

        @./docs/[guide].md
        """;

    private const string CodeReviewSkill = """
        ---
        name: code-review
        description: Reviews code changes for bugs, security problems, missing tests and maintainability. Use when the user asks for a code review, asks to check a diff, branch or pull request, or wants feedback before merging.
        ---

        # Code review

        ## Steps

        1. Find the change with `git diff` against the base branch, or read the files the user named.
        2. Understand what the change is meant to do before judging it.
        3. Read the surrounding code, callers and tests.
        4. Check each area in the list below.
        5. Confirm every finding against the code. Remove anything you cannot verify.
        6. Report findings, most severe first.

        ## What to check

        - **Correctness:** logic errors, edge cases, null values, error handling and race conditions
        - **Security:** injection, access control, secrets and unsafe input
        - **Data:** migrations, compatibility and data loss
        - **Performance:** slow paths, unbounded work and resource leaks
        - **Tests:** changed behavior has meaningful tests
        - **Maintainability:** clear names, no duplication and a focused change

        ## Output format

        For each finding:

        ```markdown
        **[Critical | High | Medium | Low]** `path/to/file.ext:line`
        Problem: what is wrong and when it happens
        Fix: the smallest correct change
        ```

        If there are no findings, say so and list what you checked.

        ## Edge cases

        - Do not report style preferences that a formatter already handles.
        - For a very large diff, review the riskiest files first and say what you skipped.
        """;

    private const string DebuggingSkill = """
        ---
        name: debugging
        description: Finds and fixes the root cause of bugs, crashes, failing tests and unexpected behavior. Use when the user reports an error, stack trace, regression, flaky test or something that does not work.
        ---

        # Debugging

        ## Steps

        1. **Reproduce.** Find the smallest reliable way to trigger the problem.
        2. **Read the evidence.** Read the full error, stack trace and logs.
        3. **Narrow it down.** Check recent changes and trace the failing path.
        4. **Form one hypothesis.** State what you think is wrong and how to test it.
        5. **Test it.** Change one thing at a time and observe the result.
        6. **Fix the root cause.** Do not hide the symptom.
        7. **Prove it.** Add a regression test that fails before the fix and passes after it.
        8. **Check for side effects.** Run the related test suite.

        ## Rules

        - Do not guess at fixes or change several things at once.
        - Do not delete, skip or weaken a test to make it pass.
        - Remove temporary logging before finishing.

        ## Output format

        ```markdown
        Root cause: what was wrong and why
        Fix: what changed, with file paths
        Verified by: the commands run and their results
        ```

        ## Edge cases

        - **Cannot reproduce:** report what you tried and what information is missing.
        - **Flaky failure:** look for timing, shared state and test order before retrying.
        """;

    private const string TestingSkill = """
        ---
        name: testing
        description: Writes and runs unit, integration and regression tests using the project's existing test framework. Use when the user asks to add tests, improve coverage, test a feature or verify a change.
        ---

        # Testing

        ## Steps

        1. Find the existing test framework, folder layout and naming pattern.
        2. List the behaviors to test: normal use, edge cases and failures.
        3. Write the smallest test for each behavior.
        4. Run the new tests and confirm that they can fail for the right reason.
        5. Run the related suite and report the result.

        ## Good tests

        - Test behavior, not private implementation details.
        - Keep each test independent, repeatable and fast.
        - Use clear names that describe the expected result.
        - Follow arrange, act, assert.
        - Use synthetic data, never real credentials or personal data.

        ## Edge cases to consider

        - Empty, null and maximum-size input
        - Invalid input and error paths
        - Boundaries, time zones and encoding
        - Concurrency, when the code is concurrent

        ## Rules

        - Do not change production code just to make a test pass unless you found a real bug.
        - Do not skip or weaken an existing test.

        ## Output format

        ```markdown
        Added: test names and file paths
        Command: the exact test command
        Result: passed, failed and skipped counts
        ```
        """;

    private const string Architecture = """
        # Architecture: [system name]

        Last updated: YYYY-MM-DD
        Owner: [name or team]

        ## 1. Introduction and Goals

        [What the system does and its most important requirements.]

        | Priority | Quality goal | Why it matters |
        | --- | --- | --- |
        | 1 | [For example: reliability] | [Reason] |

        ## 2. Constraints

        - [Technical, organizational or legal limit]

        ## 3. Context and Scope

        ```mermaid
        flowchart LR
            User[User] --> System[System]
            System --> External[External service]
        ```

        | Neighbor | Exchanges | Interface |
        | --- | --- | --- |
        | [System or user] | [Data] | [API or protocol] |

        ## 4. Solution Strategy

        - [Key technology or approach, and why]

        ## 5. Building Block View

        | Component | Responsibility | Location |
        | --- | --- | --- |
        | [Component] | [What it does] | `[path]` |

        ## 6. Runtime View

        ```mermaid
        sequenceDiagram
            User->>System: Request
            System-->>User: Response
        ```

        ## 7. Deployment View

        [Environments, infrastructure and how a release reaches them.]

        ## 8. Crosscutting Concepts

        - **Security:** [approach]
        - **Logging and monitoring:** [approach]
        - **Error handling:** [approach]

        ## 9. Architecture Decisions

        - [Link to a decision record]

        ## 10. Quality Requirements

        | Quality | Scenario | Target |
        | --- | --- | --- |
        | Performance | [Situation] | [Measurable target] |

        ## 11. Risks and Technical Debt

        | Item | Impact | Plan |
        | --- | --- | --- |
        | [Risk or debt] | High | [Action] |

        ## 12. Glossary

        | Term | Meaning |
        | --- | --- |
        | [Term] | [Definition] |
        """;

    private const string Context = """
        # Project context: [project name]

        Last updated: YYYY-MM-DD
        Owner: [name]

        ## Purpose

        [Why this project exists, in two sentences.]

        ## Problem

        [Who has the problem, how they handle it today and what it costs them.]

        ## Users

        | User | Goal | Main need |
        | --- | --- | --- |
        | [User type] | [What they want] | [What they need from this project] |

        ## Goals

        - [Measurable business goal]

        ## Non-goals

        - [What this project will not do]

        ## Stakeholders

        | Name or team | Role | Decides |
        | --- | --- | --- |
        | [Name] | [Role] | [Decisions they own] |

        ## Constraints

        - **Budget:** [limit]
        - **Timeline:** [date]
        - **Legal or compliance:** [requirement]
        - **Technology:** [required or prohibited tools]

        ## Current status

        [Where the project stands and what comes next.]

        ## Key decisions

        - YYYY-MM-DD: [Decision and reason]

        ## Terms

        | Term | Meaning here |
        | --- | --- |
        | [Term] | [Definition] |
        """;

    private const string Requirements = """
        # Requirements: [product or feature]

        Version: 0.1
        Status: Draft
        Owner: [name]
        Last updated: YYYY-MM-DD

        ## Purpose and scope

        [What this document covers and what it does not.]

        ## How to write a requirement

        Use one EARS pattern per requirement:

        | Type | Pattern |
        | --- | --- |
        | Always active | The [system] shall [response]. |
        | Event | When [trigger], the [system] shall [response]. |
        | State | While [state], the [system] shall [response]. |
        | Optional feature | Where [feature is included], the [system] shall [response]. |
        | Unwanted behavior | If [problem], then the [system] shall [response]. |

        ## Functional requirements

        | ID | Priority | Requirement | Acceptance test |
        | --- | --- | --- | --- |
        | FR-001 | Must | When [trigger], the [system] shall [response]. | [How to verify it] |
        | FR-002 | Should | If [problem], then the [system] shall [response]. | [How to verify it] |

        ## Nonfunctional requirements

        | ID | Quality | Requirement | Measure |
        | --- | --- | --- | --- |
        | NFR-001 | Performance | The [system] shall [response] within [time]. | [How to measure it] |
        | NFR-002 | Security | The [system] shall [protection]. | [How to verify it] |

        ## Assumptions and dependencies

        - [Assumption or dependency]

        ## Open questions

        - [ ] [Question] (owner: [name])
        """;
}

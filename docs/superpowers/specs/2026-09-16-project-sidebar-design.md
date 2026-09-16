# Project sidebar with build status

Date: 2026-09-16. Status: approved design, not yet implemented. Independent of the OIDC login work; implement after it.

## Goal

The left sidebar shows a TeamCity-style tree of projects and their build configurations with a live status icon on
each row, so the state of everything is visible from any page.

## Behavior

- Below the existing navigation links, a "Projects" section lists top-level projects. Projects nest by `ParentId`.
- Each project row is collapsible (chevron), shows the project name linking to `/projects/{id}`, and a status icon
  that is the worst status among its configurations and subprojects.
- Under an expanded project, each build configuration row shows its name linking to a new page
  `/build-configurations/{id}` and a status icon for its latest build.
- Status icon per configuration: latest build by `QueuedAt`. `Running` and `Queued` show a spinner; `Success` green
  check; `Failure` and `Error` red cross; `Canceled` grey dash; no builds grey dot. A paused configuration shows a
  pause glyph next to the name.
- Worst-of ordering for the project icon: Failure/Error > Running/Queued > Canceled > Success > none.
- Expanded state is remembered per browser in `localStorage`, keyed by project id.
- The tree updates live: it subscribes to `BuildEventStream.BuildUpdated` and patches the affected configuration's
  latest build in memory, then re-renders. It reloads the full tree when a project or configuration is created or
  deleted, which is signaled by a new `BuildEventStream.CatalogChanged` event raised from the project and
  configuration write paths in `ApiEndpoints` and the Blazor pages.

## Components

- `Components/Layout/ProjectTree.razor`: loads the tree once via `IDbContextFactory<CicdDbContext>`, holds a
  `List<ProjectNode>` where `ProjectNode { Project, Children, Configurations }` and `ConfigurationNode { Configuration,
  LatestBuild }`, subscribes to the stream, disposes the subscription.
- `Components/StatusIcon.razor`: maps `BuildStatus?` to an icon and title. `StatusBadge.razor` already exists for
  text badges and stays for tables.
- `Components/Pages/BuildConfigurationDetail.razor` at `/build-configurations/{id}`: name, project link, paused
  state, Run button (Developer), and the configuration's build list, reusing the table markup from `ProjectDetail`.
- `MainLayout.razor` includes `<ProjectTree />`. The sidebar becomes scrollable when the tree is tall.

## Query

One query for the tree: all projects, all configurations, and for each configuration its latest build. The latest
build is a correlated subquery (`Builds.Where(b => b.BuildConfigurationId == c.Id).OrderByDescending(b => b.QueuedAt)
.FirstOrDefault()`), which EF translates to a lateral join on PostgreSQL. Fine at build-server scale.

## Testing

Unit tests for the pure parts: worst-of status aggregation for nested projects, and the patch logic that applies a
`BuildUpdated` event to the tree (newer build replaces older; an event for a build older than the current latest is
ignored). Manual check: queue a build from the API and watch the sidebar change without a refresh.

# ADR-021: Addresses in the web app with a small router of its own

Status: Accepted · 2026-11-23

## Context

The web app was one page whose state lived in component state: a question or a plan was gone
after a reload and could not be sent to anyone (open-questions rows 101 and 102). M3.1 gives
every investigation and plan an address and adds a history page.

The app has five kinds of page. Two decisions follow: whether to add a router library, and
how addresses relate to the single-page flow that exists and is covered by browser tests.

## Decision

### Addresses

| Address | Page |
|---|---|
| `/` | The question form |
| `/investigations/{id}` | The question form with that investigation under it |
| `/plan` | Plan from an issue: the issue list and the issue screen |
| `/plans/{id}` | That plan |
| `/history` | The caller's history (ADR-020) |
| anything else | Not found |

- Asking a question or starting a plan changes the address to the new item's at once
  (`pushState`), so a reload, a bookmark or a copied address shows the same thing, and Back
  returns to the empty form or the issue.
- The issue list and the issue screen are not addresses: they are steps towards a plan, they
  are cheap to redo, and an address for an issue would need the repository and the issue
  number in it. Left out until someone misses it.
- A finished result opened by address is rendered by the same components as in the flow. An
  unfinished one is polled by the same query, so it shows progress and then the result.

### A hand-written router, no dependency

`apps/web/src/router.ts` is under two hundred lines on the History API: `parseRoute`,
`navigate`, a `useRoute` hook on `useSyncExternalStore`, and `Link`. A library was not added
because nothing here needs one: no nested routes, no loaders, no code splitting, five
patterns. Fewer dependencies is also less to keep patched in a public repository. If routes
grow parameters, nesting or data loading, replace it with a library then; the route table
above is all that would have to be carried over.

### Only a checked ID becomes part of an address

An ID is taken from an address, or put into one, only if it is a lowercase UUID as the API
writes it (`isId`). `investigationPath` and `planPath` return null for anything else and
`navigate` refuses any path that is not one of the app's addresses. So no question, title,
repository name or other text from a person, GitHub or a model can reach an address, and a
malformed ID in an address is "not found" without any request being made for it. A history
row whose ID fails the check is shown as text without a link.

### Not found is one page, and it does not say why

A malformed address, an unknown address, an ID the API does not know and an ID the caller
may not see all show the same "Page not found" page, with links to the question form and
the history. The API answers 404 for both of the last two (ADR-002), and the page adds
nothing that would tell them apart. A failure to reach the API, or any other error status,
is not "not found": it shows an error with the request's correlation reference and a way to
try again.

### Navigation that a keyboard and a screen reader can follow

- Each page sets the document title from fixed words (`History · TRACE`, `Question · TRACE`,
  `Plan · TRACE`, `Not found · TRACE`). A title never contains a question or an issue title.
- After a change of page (a link, the three buttons at the top, Back, Forward) the keyboard
  focus moves to the new page's heading, as the plan flow already did between its screens
  (M2.5). It does not move on the first load, and it does not move when the address only
  catches up with what is already on screen (right after asking or starting a plan).
- History rows are real links (`<a href>`): they work with the keyboard, a middle click,
  "open in new tab" and "copy link". A click with a modifier key is left to the browser.
- The three switches at the top stay buttons with `aria-pressed`, as M2.5 built and tested
  them. Each side remembers its last address, so switching away and back returns to the same
  item; an address that turned out to name nothing is not remembered.

### Serving deep links

The app is static files; any path that is not a file must be answered with `index.html`,
and a path under `/api` must never be.

- **Development and the browser tests**: the Vite dev server does this by default for a
  single-page app, and proxies `/api` and `/health` to the API, so an unknown API path gets
  the API's own 404. A browser test checks both directions.
- **The container** (`apps/web/Dockerfile`, `infra/nginx/default.conf.template`): nginx
  `try_files $uri /index.html` for everything else, `/api/` proxied, `/api` itself a 404,
  and a missing file under `/assets/` a 404 rather than the page.
- **The API** serves no static files and no page: every path it does not have is a 404
  problem response. Nothing changed there.

## Consequences

- The router is the project's to maintain. It is small and tested (addresses, malformed
  IDs, refusal of foreign paths), but it has no scroll restoration of its own beyond the
  browser's and no route-level code splitting.
- `/investigations/{id}` shows the question form above the result, as after asking. Someone
  opening a shared link sees a form they may not be allowed to use; the server decides.
- An issue-to-plan investigation opened at `/investigations/{id}` (the history links to
  `/plans/{id}` instead, so only a hand-made address gets there) shows the run's fixed
  summary sentence as an answer. It is correct and unhelpful.
- Filters of the history page are not in the address: a reload shows the unfiltered list.
- Running work is found again only at its own address. Showing it elsewhere after a reload
  is M3.2.

## Not verified

- The nginx configuration was exercised once by hand, with the built app and no API behind
  it (deep links 200 with the page, `/api` 404, `/assets/missing.js` 404, `/api/v1/x` 502).
  CI builds the image and does not run it.
- No person and no screen reader has used the pages; focus and titles are asserted by tests.
- Only Chromium runs the browser tests.

import { type MouseEvent, type ReactNode, useEffect } from 'react'
import { focusPageHeading, hasMoved, HISTORY_PATH, HOME_PATH, navigate } from './router.ts'

/**
 * A real link to one of the app's own pages: it has an address, so it can be opened in a new
 * tab, copied or middle-clicked. A plain click changes the page without loading it again.
 */
export function Link({ to, className, children }: { to: string; className?: string; children: ReactNode }) {
  function follow(event: MouseEvent<HTMLAnchorElement>) {
    if (event.defaultPrevented || event.button !== 0 || event.metaKey || event.ctrlKey || event.shiftKey || event.altKey) {
      return
    }
    event.preventDefault()
    navigate(to)
  }

  return (
    <a href={to} className={className} onClick={follow}>
      {children}
    </a>
  )
}

const SITE = 'TRACE'

/** Sets the document title while it is on the page. Titles are fixed words: never a question or an issue title. */
export function PageTitle({ title }: { title: string | null }) {
  useEffect(() => {
    document.title = title ? `${title} · ${SITE}` : SITE
  }, [title])
  return null
}

/** The page for an address that names nothing the caller can see. */
export function NotFound() {
  useEffect(() => {
    // Reached by moving inside the app (a link, Back, or an item that turned out not to exist):
    // the page under the keyboard is gone.
    if (hasMoved()) {
      focusPageHeading()
    }
  }, [])

  return (
    <section className="not-found" aria-labelledby="not-found-heading">
      <PageTitle title="Not found" />
      <h2 id="not-found-heading" data-page-heading tabIndex={-1}>
        Page not found
      </h2>
      <p>
        There is nothing at this address that you can open. It may never have existed, it may have been removed,
        or it belongs to a repository you do not have access to.
      </p>
      <p>
        <Link to={HOME_PATH}>Ask a question</Link> · <Link to={HISTORY_PATH}>See your history</Link>
      </p>
    </section>
  )
}

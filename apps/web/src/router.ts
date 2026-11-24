import { useSyncExternalStore } from 'react'

// A very small router on the browser's History API (ADR-021). The app has five kinds of page;
// that does not need a library. Addresses are fixed words plus, for two of them, an ID. An ID
// is only ever taken from or put into an address after `isId` accepted it, so no other text
// (a question, a title, a repository name) can become part of an address.

export type Route =
  | { name: 'home' }
  | { name: 'plan' }
  | { name: 'history' }
  | { name: 'investigation'; id: string }
  | { name: 'plan-item'; id: string }
  | { name: 'not-found' }

const ID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/

/** Whether the value is an ID as the API writes one: a lowercase UUID. Nothing else goes into an address. */
export function isId(value: unknown): value is string {
  return typeof value === 'string' && ID.test(value)
}

/** The page an address names. Anything that is not exactly one of the app's addresses is not found. */
export function parseRoute(pathname: string): Route {
  // One trailing slash is forgiven; the upper-case form of an ID is the same ID.
  const path = /[^/]\/$/.test(pathname) ? pathname.slice(0, -1) : pathname
  if (path === '/' || path === '') {
    return { name: 'home' }
  }
  if (path === '/plan') {
    return { name: 'plan' }
  }
  if (path === '/history') {
    return { name: 'history' }
  }

  const item = /^\/(investigations|plans)\/([^/]+)$/.exec(path)
  const id = item?.[2].toLowerCase()
  if (item && isId(id)) {
    return item[1] === 'plans' ? { name: 'plan-item', id } : { name: 'investigation', id }
  }
  return { name: 'not-found' }
}

export const HOME_PATH = '/'
export const PLAN_PATH = '/plan'
export const HISTORY_PATH = '/history'

/** The address of one investigation, or null when the ID is not one. */
export function investigationPath(id: unknown): string | null {
  return isId(id) ? `/investigations/${id}` : null
}

/** The address of one plan, or null when the ID is not one. */
export function planPath(id: unknown): string | null {
  return isId(id) ? `/plans/${id}` : null
}

export function pathFor(route: Route): string | null {
  switch (route.name) {
    case 'home':
      return HOME_PATH
    case 'plan':
      return PLAN_PATH
    case 'history':
      return HISTORY_PATH
    case 'investigation':
      return investigationPath(route.id)
    case 'plan-item':
      return planPath(route.id)
    default:
      return null
  }
}

// How many times the page changed while the app was open, and how many of those changes should
// move the keyboard to the new page's heading. The first page load is neither.
let moves = 0
let focusRequests = 0
const listeners = new Set<() => void>()

function emit() {
  for (const listener of listeners) {
    listener()
  }
}

function onPopState() {
  // Back and forward show another page: the keyboard goes to its heading.
  moves++
  focusRequests++
  emit()
}

function subscribe(listener: () => void): () => void {
  if (listeners.size === 0) {
    window.addEventListener('popstate', onPopState)
  }
  listeners.add(listener)
  return () => {
    listeners.delete(listener)
    if (listeners.size === 0) {
      window.removeEventListener('popstate', onPopState)
    }
  }
}

/**
 * Goes to one of the app's own addresses without loading the page again.
 * `focus: false` is for when the address only catches up with what is already on the screen
 * (the item that was just created): nothing moved, so the keyboard stays where it is.
 */
export function navigate(path: string, options: { replace?: boolean; focus?: boolean } = {}) {
  // Only an address this router knows. A path built from anything else is a programming error.
  if (parseRoute(path).name === 'not-found') {
    throw new Error('navigate() was given a path that is not one of the app’s addresses.')
  }

  if (path !== window.location.pathname) {
    if (options.replace) {
      window.history.replaceState(null, '', path)
    } else {
      window.history.pushState(null, '', path)
    }
  }
  moves++
  if (options.focus !== false) {
    focusRequests++
  }
  emit()
}

/** Whether the app has changed page since it was loaded. */
export function hasMoved(): boolean {
  return moves > 0
}

function snapshot(): string {
  return `${focusRequests} ${window.location.pathname}`
}

/** The current page, and a number that goes up each time the keyboard should move to its heading. */
export function useRoute(): { route: Route; path: string; focusRequest: number } {
  const value = useSyncExternalStore(subscribe, snapshot)
  const space = value.indexOf(' ')
  const path = value.slice(space + 1)
  return { route: parseRoute(path), path, focusRequest: Number(value.slice(0, space)) }
}

/** Puts the keyboard at the heading of the page that is showing. Hidden pages are passed over. */
export function focusPageHeading() {
  for (const heading of document.querySelectorAll<HTMLElement>('[data-page-heading]')) {
    if (heading.closest('[hidden]') === null) {
      heading.focus()
      return
    }
  }
}

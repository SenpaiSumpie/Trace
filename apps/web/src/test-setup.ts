import { afterEach } from 'vitest'

// The app keeps its place in the address. Every test starts at the home page, whatever the
// test before it navigated to.
afterEach(() => {
  window.history.replaceState(null, '', '/')
  document.title = 'TRACE'
})

import { expect, test } from 'vitest'
import { investigationPath, isId, navigate, parseRoute, pathFor, planPath } from './router.ts'

const ID = '0193a5c0-1111-7000-8000-00000000000a'

test('each address names its page', () => {
  expect(parseRoute('/')).toEqual({ name: 'home' })
  expect(parseRoute('/plan')).toEqual({ name: 'plan' })
  expect(parseRoute('/history')).toEqual({ name: 'history' })
  expect(parseRoute(`/investigations/${ID}`)).toEqual({ name: 'investigation', id: ID })
  expect(parseRoute(`/plans/${ID}`)).toEqual({ name: 'plan-item', id: ID })
})

test('one trailing slash and an upper-case ID are the same address', () => {
  expect(parseRoute('/history/')).toEqual({ name: 'history' })
  expect(parseRoute(`/plans/${ID}/`)).toEqual({ name: 'plan-item', id: ID })
  expect(parseRoute(`/investigations/${ID.toUpperCase()}`)).toEqual({ name: 'investigation', id: ID })
})

test.each([
  '/nothing-here',
  '/history/extra',
  '/plan/5',
  '/plans',
  '/plans/',
  '/investigations',
  '/Investigations/' + ID,
  '/api/v1/investigations',
  '//',
  '/history//',
])('an address that is not one of the app’s is not found: %s', (path) => {
  expect(parseRoute(path)).toEqual({ name: 'not-found' })
})

test.each([
  'inv-1',
  '1',
  ID.slice(0, -1),
  `${ID}0`,
  ID.replaceAll('-', ''),
  `{${ID}}`,
  ID.replace('a', 'g'),
  '..',
  '%2e%2e',
  '<script>',
  `${ID}%0a`,
  `${ID}/events`,
  `${ID}?x=1`,
  ` ${ID}`,
])('a malformed ID is not found and never an item: %s', (id) => {
  expect(parseRoute(`/investigations/${id}`)).toEqual({ name: 'not-found' })
  expect(parseRoute(`/plans/${id}`)).toEqual({ name: 'not-found' })
})

test('only a lowercase UUID is an ID', () => {
  expect(isId(ID)).toBe(true)
  expect(isId(ID.toUpperCase())).toBe(false)
  expect(isId(`${ID}\n`)).toBe(false)
  expect(isId(`javascript:${ID}`)).toBe(false)
  expect(isId('')).toBe(false)
  expect(isId(null)).toBe(false)
  expect(isId(undefined)).toBe(false)
  expect(isId(42)).toBe(false)
  expect(isId({ toString: () => ID })).toBe(false)
})

test('an address is only built from an ID that passed the check', () => {
  expect(investigationPath(ID)).toBe(`/investigations/${ID}`)
  expect(planPath(ID)).toBe(`/plans/${ID}`)
  for (const hostile of ['inv-1', '../history', `${ID}/../../api/v1/auth/logout`, 'javascript:alert(1)', '//attacker.invalid', null, undefined]) {
    expect(investigationPath(hostile)).toBeNull()
    expect(planPath(hostile)).toBeNull()
  }
})

test('every page has one address and it leads back to the page', () => {
  for (const path of ['/', '/plan', '/history', `/investigations/${ID}`, `/plans/${ID}`]) {
    expect(pathFor(parseRoute(path))).toBe(path)
  }
  expect(pathFor({ name: 'not-found' })).toBeNull()
  expect(pathFor({ name: 'investigation', id: 'not-an-id' })).toBeNull()
})

test('navigating changes the address and refuses anything that is not one of the app’s addresses', () => {
  navigate('/history')
  expect(window.location.pathname).toBe('/history')

  for (const path of ['https://attacker.invalid/', '//attacker.invalid', '/api/v1/auth/logout', '/investigations/inv-1', 'javascript:alert(1)']) {
    expect(() => navigate(path)).toThrow()
  }
  expect(window.location.pathname).toBe('/history')
  expect(window.location.host).toBe('localhost:3000')
})

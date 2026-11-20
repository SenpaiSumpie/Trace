import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { cleanup, render, screen } from '@testing-library/react'
import { afterEach, expect, test, vi } from 'vitest'
import { ApiStatus } from './ApiStatus.tsx'

function renderWithHealthResponse(response: Response | Error) {
  vi.stubGlobal(
    'fetch',
    vi.fn(() =>
      response instanceof Error ? Promise.reject(response) : Promise.resolve(response),
    ),
  )
  render(
    <QueryClientProvider client={new QueryClient()}>
      <ApiStatus />
    </QueryClientProvider>,
  )
}

afterEach(() => {
  cleanup()
  vi.unstubAllGlobals()
})

test('reports a healthy API', async () => {
  renderWithHealthResponse(new Response('Healthy'))

  expect(await screen.findByText('API healthy')).toBeTruthy()
})

test('reports an unreachable API when the request fails', async () => {
  renderWithHealthResponse(new TypeError('Failed to fetch'))

  expect(await screen.findByText('API unreachable')).toBeTruthy()
})

test('reports an unreachable API on an error status', async () => {
  renderWithHealthResponse(new Response('Unhealthy', { status: 503 }))

  expect(await screen.findByText('API unreachable')).toBeTruthy()
})

test('does not trust a 200 response that is not the health payload', async () => {
  renderWithHealthResponse(new Response('<!doctype html>'))

  expect(await screen.findByText('API unreachable')).toBeTruthy()
})

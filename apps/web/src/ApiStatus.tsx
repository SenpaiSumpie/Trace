import { useQuery } from '@tanstack/react-query'

async function fetchApiHealth(): Promise<string> {
  const response = await fetch('/health')
  if (!response.ok) {
    throw new Error(`API health check failed with status ${response.status}`)
  }
  return (await response.text()).trim()
}

export function ApiStatus() {
  const health = useQuery({
    queryKey: ['api-health'],
    queryFn: fetchApiHealth,
    retry: false,
  })

  if (health.isPending) {
    return <p role="status">Checking API…</p>
  }

  if (health.isError || health.data !== 'Healthy') {
    return (
      <p role="status" className="status status-down">
        API unreachable
      </p>
    )
  }

  return (
    <p role="status" className="status status-up">
      API healthy
    </p>
  )
}

export interface TaskItem {
  id: number
  title: string
  createdAt: string
  completed: boolean
}

export interface TaskPage {
  items: TaskItem[]
  page: number
  pageSize: number
  totalCount: number
}

const apiKey = import.meta.env.VITE_TASKBOARD_API_KEY ?? ''

async function request<T>(path: string, init?: RequestInit): Promise<T> {
  const response = await fetch(path, {
    ...init,
    headers: { 'Content-Type': 'application/json', 'X-Api-Key': apiKey, ...init?.headers },
  })
  if (!response.ok) {
    throw new Error(`TaskBoard API returned ${response.status}`)
  }
  return (await response.json()) as T
}

export function listTasks(page: number, pageSize = 20): Promise<TaskPage> {
  return request<TaskPage>(`/tasks?page=${page}&pageSize=${pageSize}`)
}

export function createTask(title: string): Promise<TaskItem> {
  return request<TaskItem>('/tasks', { method: 'POST', body: JSON.stringify({ title }) })
}

export function queueReport(): Promise<{ reportId: string }> {
  return request<{ reportId: string }>('/reports', { method: 'POST' })
}

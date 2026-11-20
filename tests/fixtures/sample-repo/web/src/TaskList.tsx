import { useEffect, useState } from 'react'
import { listTasks, type TaskPage } from './api'

const PAGE_SIZE = 20

export function TaskList() {
  const [page, setPage] = useState(1)
  const [data, setData] = useState<TaskPage | null>(null)
  const [error, setError] = useState<string | null>(null)

  useEffect(() => {
    let cancelled = false
    setError(null)
    listTasks(page, PAGE_SIZE)
      .then((result) => {
        if (!cancelled) {
          setData(result)
        }
      })
      .catch((reason: Error) => {
        if (!cancelled) {
          setError(reason.message)
        }
      })
    return () => {
      cancelled = true
    }
  }, [page])

  if (error) {
    return <p role="alert">Could not load tasks: {error}</p>
  }

  if (!data) {
    return <p>Loading tasks…</p>
  }

  const lastPage = Math.max(1, Math.ceil(data.totalCount / data.pageSize))

  return (
    <section>
      <h2>Tasks</h2>
      <ul>
        {data.items.map((task) => (
          <li key={task.id}>
            {task.completed ? <s>{task.title}</s> : task.title}
          </li>
        ))}
      </ul>
      <nav aria-label="Pagination">
        <button disabled={page <= 1} onClick={() => setPage(page - 1)}>
          Previous
        </button>
        <span>
          Page {page} of {lastPage}
        </span>
        <button disabled={page >= lastPage} onClick={() => setPage(page + 1)}>
          Next
        </button>
      </nav>
    </section>
  )
}

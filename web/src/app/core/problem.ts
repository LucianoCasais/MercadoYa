import { HttpErrorResponse } from '@angular/common/http';

/** Que paso, para decidir que mostrar u ofrecer (reintentar, iniciar sesion, etc.). */
export type ProblemKind = 'offline' | 'session' | 'forbidden' | 'notFound' | 'conflict' | 'validation' | 'unavailable' | 'server' | 'other';

/** Lectura de los errores ProblemDetails / ValidationProblem que devuelve la API. */
export interface ApiProblem {
  kind: ProblemKind;
  /** Mensaje general para mostrar al usuario. */
  message: string;
  /** Errores por campo (claves en camelCase, como las manda la API: name, priceCents, ...). */
  fields: Record<string, string>;
  /** Tiene sentido ofrecer "Reintentar": la falla es de conexion o del servidor, no del pedido. */
  retryable: boolean;
}

/** Sin conexion o servicio caido/sobrecargado: suele resolverse solo al rato. */
export const isTransient = (status: number) => status === 0 || status === 502 || status === 503 || status === 504;

export function readProblem(e: HttpErrorResponse): ApiProblem {
  const body = (e.error && typeof e.error === 'object' ? e.error : {}) as { title?: string; detail?: string; errors?: Record<string, string[]> };
  const fields: Record<string, string> = {};
  for (const [k, v] of Object.entries(body.errors ?? {})) {
    if (v?.length) fields[k.charAt(0).toLowerCase() + k.slice(1)] = v[0];
  }
  const own = body.detail || body.title;

  const problem = (kind: ProblemKind, message: string, retryable = false): ApiProblem => ({ kind, message, fields, retryable });
  switch (true) {
    case e.status === 0:
      return problem('offline', 'No pudimos conectar con el servidor. Revisá tu conexión y probá de nuevo.', true);
    case e.status === 401:
      return problem('session', 'Tu sesión venció. Volvé a iniciar sesión.');
    case e.status === 403:
      return problem('forbidden', 'Tu usuario no tiene permiso para hacer esto.');
    case e.status === 404:
      return problem('notFound', own || 'No encontramos lo que buscabas.');
    case e.status === 409:
      return problem('conflict', own || 'Los datos cambiaron mientras los mirabas. Volvé a cargarlos.');
    case Object.keys(fields).length > 0:
      return problem('validation', 'Revisá los datos marcados.');
    case isTransient(e.status):
      // La API explica algunos 503 (por ejemplo, la cola de pedidos caida): se usa su mensaje.
      return problem('unavailable', (e.status === 503 && body.detail) || 'El servicio no está disponible en este momento. Probá de nuevo en unos minutos.', true);
    case e.status >= 500:
      return problem('server', 'Ocurrió un error en el servidor. Probá de nuevo en unos minutos.', true);
    default:
      return problem('other', own || 'Ocurrió un error inesperado. Probá de nuevo.');
  }
}

import { HttpErrorResponse } from '@angular/common/http';

/** Lectura de los errores ProblemDetails / ValidationProblem que devuelve la API. */
export interface ApiProblem {
  /** Mensaje general para mostrar al usuario. */
  message: string;
  /** Errores por campo (claves en camelCase, como las manda la API: name, priceCents, ...). */
  fields: Record<string, string>;
}

export function readProblem(e: HttpErrorResponse): ApiProblem {
  const body = (e.error ?? {}) as { title?: string; detail?: string; errors?: Record<string, string[]> };
  const fields: Record<string, string> = {};
  for (const [k, v] of Object.entries(body.errors ?? {})) {
    if (v?.length) fields[k.charAt(0).toLowerCase() + k.slice(1)] = v[0];
  }

  let message: string;
  if (e.status === 0) message = 'No pudimos conectar con la API. ¿Está levantada?';
  else if (e.status === 401) message = 'Tu sesión venció. Volvé a iniciar sesión.';
  else if (e.status === 403) message = 'Tu usuario no tiene permisos de administrador.';
  else if (Object.keys(fields).length) message = 'Revisá los datos marcados.';
  else message = body.detail || body.title || 'Ocurrió un error inesperado. Probá de nuevo.';
  return { message, fields };
}

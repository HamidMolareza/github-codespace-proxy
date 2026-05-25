import { createReadStream, promises as fs } from 'node:fs';
import http from 'node:http';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const port = Number(process.env.PORT ?? '8080');
const apiBaseUrl = new URL(process.env.API_BASE_URL ?? 'http://backend:8080');
const modulePath = fileURLToPath(import.meta.url);
const root = path.join(path.dirname(modulePath), 'dist');

const contentTypes = new Map([
  ['.css', 'text/css; charset=utf-8'],
  ['.html', 'text/html; charset=utf-8'],
  ['.js', 'text/javascript; charset=utf-8'],
  ['.json', 'application/json; charset=utf-8'],
  ['.svg', 'image/svg+xml'],
  ['.txt', 'text/plain; charset=utf-8']
]);

const server = http.createServer(async (request, response) => {
  if (!request.url || !request.method) {
    response.writeHead(400);
    response.end();
    return;
  }

  if (isApiRequestUrl(request.url)) {
    await proxyApi(request, response);
    return;
  }

  await serveStatic(request.url, response);
});

if (process.argv[1] && path.resolve(process.argv[1]) === modulePath) {
  server.listen(port, '0.0.0.0');
}

async function proxyApi(request, response) {
  const target = createApiTargetUrl(request.url);
  if (!target) {
    response.writeHead(400, { 'content-type': 'application/json; charset=utf-8' });
    response.end(JSON.stringify({ error: 'Invalid API request path.' }));
    return;
  }

  const headers = new Headers();
  for (const [key, value] of Object.entries(request.headers)) {
    if (value !== undefined) {
      headers.set(key, Array.isArray(value) ? value.join(',') : value);
    }
  }
  headers.set('host', apiBaseUrl.host);

  try {
    const upstream = await fetch(target, {
      method: request.method,
      headers,
      body: request.method === 'GET' || request.method === 'HEAD' ? undefined : request,
      duplex: 'half'
    });

    response.writeHead(upstream.status, Object.fromEntries(upstream.headers));
    if (upstream.body) {
      for await (const chunk of upstream.body) {
        response.write(chunk);
      }
    }
    response.end();
  } catch {
    response.writeHead(502, { 'content-type': 'application/json; charset=utf-8' });
    response.end(JSON.stringify({ error: 'Backend API is unavailable.' }));
  }
}

function isApiRequestUrl(requestUrl) {
  return createApiTargetUrl(requestUrl) !== null;
}

export function createApiTargetUrl(requestUrl, baseUrl = apiBaseUrl) {
  try {
    const requestPath = new URL(requestUrl ?? '/', 'http://localhost');
    if (!requestPath.pathname.startsWith('/api/')) {
      return null;
    }

    const target = new URL(baseUrl.origin);
    target.pathname = requestPath.pathname;
    target.search = requestPath.search;
    return target;
  } catch {
    return null;
  }
}

async function serveStatic(url, response) {
  const pathname = decodeURIComponent(new URL(url, 'http://localhost').pathname);
  const safePath = path.normalize(pathname).replace(/^(\.\.[/\\])+/, '');
  const requestedPath = path.join(root, safePath);
  const filePath = await resolveFile(requestedPath);
  const ext = path.extname(filePath);

  response.writeHead(200, {
    'content-type': contentTypes.get(ext) ?? 'application/octet-stream',
    'cache-control': ext === '.html' ? 'no-cache' : 'public, max-age=31536000, immutable'
  });
  createReadStream(filePath).pipe(response);
}

async function resolveFile(requestedPath) {
  try {
    const stat = await fs.stat(requestedPath);
    if (stat.isFile()) {
      return requestedPath;
    }
  } catch {
    return path.join(root, 'index.html');
  }

  return path.join(root, 'index.html');
}

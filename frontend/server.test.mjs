import assert from 'node:assert/strict';
import test from 'node:test';
import { createApiTargetUrl } from './server.mjs';

const backend = new URL('http://backend.internal:5080');

test('createApiTargetUrl preserves API path and query on the backend origin', () => {
  const target = createApiTargetUrl('/api/local-proxy/status?refresh=true', backend);

  assert.equal(target?.href, 'http://backend.internal:5080/api/local-proxy/status?refresh=true');
});

test('createApiTargetUrl ignores attacker-controlled absolute request origins', () => {
  const target = createApiTargetUrl('http://evil.test/api/local-proxy/status?refresh=true', backend);

  assert.equal(target?.origin, 'http://backend.internal:5080');
  assert.equal(target?.pathname, '/api/local-proxy/status');
  assert.equal(target?.search, '?refresh=true');
});

test('createApiTargetUrl rejects normalized paths outside the API prefix', () => {
  const target = createApiTargetUrl('/api/../admin', backend);

  assert.equal(target, null);
});

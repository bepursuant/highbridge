import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = join(dirname(fileURLToPath(import.meta.url)), '..', '..', '..');

test('the GitHub Pages copy of the client matches clients/js', () => {
  for (const name of ['highbridge.js', 'highbridge.d.ts']) {
    const source = readFileSync(join(root, 'clients', 'js', name));
    const published = readFileSync(join(root, 'docs', 'lib', name));
    assert.deepEqual(published, source, `docs/lib/${name} is stale. Copy clients/js/${name} over it.`);
  }
});

'use strict';

// Run browser checks against the synthetic fixture, never a production account.
const { spawn, execFile } = require('node:child_process');
const { promisify } = require('node:util');
const path = require('node:path');
const run = promisify(execFile);
const root = path.resolve(__dirname, '..');

async function main() {
  const fixture = spawn(path.join(root, 'artifacts/tests/WebDashboardSmoke.exe'), ['--serve'], {
    cwd: root, windowsHide: true, stdio: ['pipe', 'pipe', 'pipe'],
  });
  let diagnostics = '';
  fixture.stderr.on('data', chunk => { diagnostics += chunk; });
  try {
    await new Promise((resolve, reject) => {
      let output = '';
      const timeout = setTimeout(() => reject(new Error('Synthetic web fixture did not start within 20 seconds. ' + diagnostics)), 20000);
      const finish = error => {
        clearTimeout(timeout);
        fixture.removeListener('error', fail);
        fixture.removeListener('exit', exited);
        fixture.stdout.removeListener('data', receive);
        error ? reject(error) : resolve();
      };
      const fail = error => finish(error);
      const exited = code => finish(new Error('Synthetic web fixture exited (' + code + '). ' + diagnostics));
      const receive = chunk => {
        output += chunk;
        if (output.includes('http://127.0.0.1:')) finish();
      };
      fixture.on('error', fail);
      fixture.on('exit', exited);
      fixture.stdout.on('data', receive);
    });
    const scripts = process.argv.slice(2);
    if (!scripts.length) scripts.push('scripts/test-web.cjs');
    for (const script of scripts) {
      const result = await run(process.execPath, [path.resolve(root, script)], {
        cwd: root, windowsHide: true, timeout: 90000, maxBuffer: 2 * 1024 * 1024,
      });
      process.stdout.write(result.stdout);
      process.stderr.write(result.stderr);
    }
  } finally {
    // Only terminate the fixture this runner created; no production process lookup.
    if (fixture.exitCode === null) fixture.kill();
  }
}

main().catch(error => {
  console.error(error.stderr || error.stack || error.message);
  process.exitCode = 1;
});

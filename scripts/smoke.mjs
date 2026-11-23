import assert from 'node:assert/strict';

const api = (process.argv[2] ?? 'http://127.0.0.1:5080').replace(/\/$/, '');

function client() {
  let cookie;
  return async (method, route, expected, json) => {
    const response = await fetch(`${api}/api${route}`, {
      method,
      headers: { ...(cookie && { cookie }), ...(json !== undefined && { 'content-type': 'application/json' }) },
      body: json === undefined ? undefined : JSON.stringify(json),
    });
    const payload = await response.text();
    assert.ok([expected].flat().includes(response.status),
      `${method} ${route}: expected ${expected}, received ${response.status} ${payload}`);
    const session = response.headers.getSetCookie()[0];
    if (session) cookie = session.split(';')[0];
    return { status: response.status, body: payload ? JSON.parse(payload) : undefined, headers: response.headers };
  };
}

const anonymous = client();
const claimant = client();
const otherClaimant = client();
const officer = client();
const otherOfficer = client();

await anonymous('GET', '/health', 200);
await anonymous('GET', '/claims', 401);
await anonymous('POST', '/session', 401, { account: 'officer-a', password: 'incorrect' });
for (const [request, account] of [[claimant, 'claimant-a'], [otherClaimant, 'claimant-b'],
  [officer, 'officer-a'], [otherOfficer, 'officer-b']]) {
  const signedIn = await request('POST', '/session', 200, { account, password: 'claims-demo' });
  assert.equal(signedIn.body.id, account);
  assert.match(signedIn.headers.get('set-cookie'), /httponly/i);
  assert.equal((await request('GET', '/session', 200)).body.id, account);
}
await claimant('GET', '/workload', 403);
const initial = (await officer('GET', '/workload', 200)).body;
const draft = { category: 'motor', incidentDate: '2024-11-17', description: 'Synthetic collision for the API smoke check.', reportedLoss: 375.25 };
await claimant('POST', '/claims', 400, { ...draft, reportedLoss: 0.001 });
await claimant('POST', '/claims', 400, { ...draft, incidentDate: '2099-01-01' });
await claimant('POST', '/claims', 400, { ...draft, description: '  ' });
await officer('POST', '/claims', 403, draft);

const created = await claimant('POST', '/claims', 201, draft);
const path = `/claims/${created.body.claim.id}`;
assert.equal(created.headers.get('location'), `/api${path}`);
assert.equal(created.body.claim.status, 'submitted');
assert.equal(created.body.claim.reportedLossMinor, 37525);
await otherClaimant('GET', path, 404);
assert.ok(!(await otherClaimant('GET', '/claims', 200)).body.some(claim => claim.id === created.body.claim.id));
await claimant('POST', `${path}/assignment`, 403, { expectedVersion: 1 });
await claimant('POST', `${path}/information-response`, 409, { expectedVersion: 1, note: 'Too early' });

const assignment = await Promise.all([officer, otherOfficer].map(request =>
  request('POST', `${path}/assignment`, [200, 409], { expectedVersion: 1 })));
assert.deepEqual(assignment.map(result => result.status).sort(), [200, 409]);
const winner = assignment.findIndex(result => result.status === 200);
const [assigned, unassigned] = winner === 0 ? [officer, otherOfficer] : [otherOfficer, officer];
await unassigned('POST', `${path}/decision`, 409, { expectedVersion: 2, status: 'rejected', note: 'Wrong officer' });
await assigned('POST', `${path}/information-request`, 400, { expectedVersion: 2, note: '  ' });

const asked = (await assigned('POST', `${path}/information-request`, 200,
  { expectedVersion: 2, note: 'Please provide the repair estimate.' })).body;
assert.equal(asked.claim.status, 'informationRequested');
await assigned('POST', `${path}/decision`, 409, { expectedVersion: 3, status: 'settled', note: 'Too early' });
await otherClaimant('POST', `${path}/information-response`, 404, { expectedVersion: 3, note: 'Wrong claimant' });
const responded = (await claimant('POST', `${path}/information-response`, 200,
  { expectedVersion: 3, note: 'The repair estimate is MYR 375.25.' })).body;
assert.equal(responded.claim.status, 'inReview');
await assigned('POST', `${path}/decision`, 409, { expectedVersion: 3, status: 'settled', note: 'Stale version' });

const open = (await officer('GET', '/workload', 200)).body;
assert.equal(open.inReview, initial.inReview + 1);
assert.equal(open.outstandingReportedLossMinor, initial.outstandingReportedLossMinor + 37525);
await assigned('POST', `${path}/decision`, 200, { expectedVersion: 4, status: 'settled', note: 'The estimate was accepted.' });
await assigned('POST', `${path}/decision`, 409, { expectedVersion: 5, status: 'rejected', note: 'Already final' });
const finalClaim = (await claimant('GET', path, 200)).body;
assert.deepEqual(finalClaim.history.map(entry => entry.status),
  ['submitted', 'inReview', 'informationRequested', 'inReview', 'settled']);
assert.equal(finalClaim.history.at(-1).note, 'The estimate was accepted.');
assert.equal(finalClaim.claim.version, 5);

const secondPath = `/claims/${(await claimant('POST', '/claims', 201, { ...draft, category: 'property' })).body.claim.id}`;
await officer('POST', `${secondPath}/assignment`, 200, { expectedVersion: 1 });
await officer('POST', `${secondPath}/decision`, 200, { expectedVersion: 2, status: 'rejected', note: 'Excluded incident.' });
assert.equal((await claimant('GET', secondPath, 200)).body.claim.status, 'rejected');
const completed = (await officer('GET', '/workload', 200)).body;
assert.equal(completed.outstandingReportedLossMinor, initial.outstandingReportedLossMinor);
assert.equal(completed.settled, initial.settled + 1);
assert.equal(completed.rejected, initial.rejected + 1);
await claimant('DELETE', '/session', 204);
await claimant('GET', path, 401);

console.log(`Claims smoke check passed. Settlement: ${finalClaim.claim.id}. Rejection: ${secondPath.split('/').at(-1)}.`);

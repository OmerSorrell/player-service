const fs = require('fs');
const root = __dirname;
const files = fs.readdirSync(root).filter(x => /^run-\d+-(true|false)\.json$/.test(x)).sort();
const runs = files.map(file => ({file, enabled:file.includes('true'), ...JSON.parse(fs.readFileSync(`${root}/${file}`,'utf8'))}));
const median = a => [...a].sort((a,b)=>a-b)[Math.floor(a.length/2)];
for (const enabled of [false,true]) {
  const group = runs.filter(r=>r.enabled===enabled);
  if (!group.length) continue;
  const count = (kind, status) => group.reduce((n,r)=>n+(r.mixed[kind+'Statuses'][status]||0),0);
  console.log(JSON.stringify({enabled, runs:group.length,
    baselineP99:group.map(r=>r.baseline.cold.p99Ms),
    coldP99:group.map(r=>r.mixed.cold.p99Ms),
    medianColdP50:median(group.map(r=>r.mixed.cold.p50Ms)),
    medianColdP95:median(group.map(r=>r.mixed.cold.p95Ms)),
    medianColdP99:median(group.map(r=>r.mixed.cold.p99Ms)),
    medianHotP99:median(group.map(r=>r.mixed.hot.p99Ms)),
    hotSuccessRps: group.reduce((n,r)=>n+r.mixed.hot.requests,0)/group.reduce((n,r)=>n+r.mixed.elapsedSeconds,0),
    cold200:count('cold','200'),cold429:count('cold','429'),hot200:count('hot','200'),hot429:count('hot','429'),
    allConserved:group.every(r=>r.conservedPoints===34000)
  },null,2));
}

const REPO_RAW = 'https://raw.githubusercontent.com/Dr-Dark92/just-another-ark-server-manager/phase-2-asa-bootstrap/src/JAASM.App/Data/';
const FILES = [
  ['items', 'ark-items.json', 'items'],
  ['engrams', 'ark-engrams.json', 'engrams'],
];

const statusEl = document.getElementById('status');
const startBtn = document.getElementById('start');
const sourceEl = document.getElementById('source');

function sleep(ms) {
  return new Promise(resolve => setTimeout(resolve, ms));
}

function safeName(name) {
  return (name || 'unnamed').replace(/[\\/:*?"<>|]/g, '_').trim();
}

function wikiStem(name) {
  return String(name || '').trim().replace(/ /g, '_');
}

function log(message) {
  statusEl.textContent += `\n${message}`;
  statusEl.scrollTop = statusEl.scrollHeight;
}

async function loadCatalogue() {
  const jobs = [];
  for (const [kind, jsonFile, arrayName] of FILES) {
    const response = await fetch(REPO_RAW + jsonFile, { cache: 'no-store' });
    if (!response.ok) throw new Error(`Failed to load ${jsonFile}: HTTP ${response.status}`);
    const doc = await response.json();
    for (const entry of (doc[arrayName] || [])) {
      if (!entry.displayName) continue;
      jobs.push({ kind, name: entry.displayName });
    }
  }
  return jobs;
}

function directUrl(source, name) {
  const base = source === 'official'
    ? 'https://ark.wiki.gg/wiki/Special:Redirect/file/'
    : 'https://ark.fandom.com/wiki/Special:Redirect/file/';
  return base + encodeURIComponent(`${wikiStem(name)}.png`);
}

async function downloadOne(source, job, index, total) {
  const url = directUrl(source, job.name);
  const filename = `JAASM-ARK-Icons/${job.kind}/${safeName(job.name)}.png`;

  await chrome.downloads.download({
    url,
    filename,
    conflictAction: 'overwrite',
    saveAs: false,
  });

  log(`[${String(index).padStart(3, '0')}/${String(total).padStart(3, '0')}] queued ${job.kind} ${job.name}`);
}

startBtn.addEventListener('click', async () => {
  startBtn.disabled = true;
  statusEl.textContent = 'Loading JAASM catalogue...';

  try {
    const source = sourceEl.value;
    const jobs = await loadCatalogue();
    log(`Loaded ${jobs.length} entries.`);
    log(`Source: ${source}`);
    log('Downloads are being sent through the browser download manager.');

    let queued = 0;
    let failed = 0;

    for (let i = 0; i < jobs.length; i++) {
      try {
        await downloadOne(source, jobs[i], i + 1, jobs.length);
        queued++;
      } catch (err) {
        failed++;
        log(`[${String(i + 1).padStart(3, '0')}/${String(jobs.length).padStart(3, '0')}] FAILED ${jobs[i].kind} ${jobs[i].name}: ${err}`);
      }
      await sleep(300);
    }

    log('');
    log(`Finished queueing. queued=${queued} failed=${failed}`);
    log('Check Downloads/JAASM-ARK-Icons/items and engr​ams.');
    log('Important: a queued download can still be an HTML error page if the wiki rejects that exact file name. Review a few files before zipping them.');
  } catch (err) {
    log(`FATAL: ${err}`);
  } finally {
    startBtn.disabled = false;
  }
});

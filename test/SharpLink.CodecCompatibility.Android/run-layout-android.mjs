import fs from 'node:fs/promises';
import path from 'node:path';
import { spawnSync } from 'node:child_process';
import { loadLayoutEnvelopes, writeLayoutCorpus } from '../SharpLink.CodecCompatibility.Browser/layout-artifacts.mjs';

const packageName = 'com.sharplink.codeccompat';
const activityName = 'com.sharplink.codeccompat.LayoutEvidenceActivity';
const inputFile = 'files/sharplink-input.json';
const resultFile = 'files/sharplink-result.json';

function adb(args, options = {}) {
    const result = spawnSync('adb', args, { encoding: 'utf8', ...options });
    if (result.status !== 0) throw new Error(`adb ${args.join(' ')} failed (${result.status}):\n${result.stdout ?? ''}\n${result.stderr ?? ''}`);
    return result.stdout ?? '';
}
function adbTry(args, options = {}) { return spawnSync('adb', args, { encoding: 'utf8', ...options }); }
function delay(ms) { return new Promise(resolve => setTimeout(resolve, ms)); }

async function waitForResult(launchOutput) {
    const deadline = Date.now() + 120_000;
    let lastRead = null;
    let lastParseError = null;
    while (Date.now() < deadline) {
        if (adbTry(['shell','run-as',packageName,'test','-f',resultFile]).status === 0) {
            const read = adbTry(['shell','run-as',packageName,'cat',resultFile]);
            lastRead = read;
            if (read.status === 0) {
                const text = read.stdout ?? '';
                try {
                    JSON.parse(text);
                    return text;
                } catch (error) {
                    lastParseError = error;
                }
            }
        }
        await delay(250);
    }
    const logcat = adbTry(['logcat','-d','-t','2000']);
    const readDiagnostics = lastRead is null
        ? 'result read was never attempted successfully after the file probe'
        : `last result read status: ${lastRead.status}\nstdout:\n${lastRead.stdout ?? ''}\nstderr:\n${lastRead.stderr ?? ''}`;
    const parseDiagnostics = lastParseError is null
        ? ''
        : `\nlast JSON parse error:\n${lastParseError.stack ?? lastParseError}`;
    throw new Error(`Android layout probe timed out.\nam start:\n${launchOutput}\n${readDiagnostics}${parseDiagnostics}\nlogcat:\n${logcat.stdout ?? ''}\n${logcat.stderr ?? ''}`);
}

async function run(mode, producerRoot, outputPath, profile, commit, sdk, runtimeFamily) {
    const input = mode === 'verify' ? JSON.stringify(await loadLayoutEnvelopes(producerRoot)) : null;
    adb(['shell','am','force-stop',packageName]);
    adb(['shell','run-as',packageName,'mkdir','-p','files']);
    adbTry(['shell','run-as',packageName,'rm','-f',inputFile,resultFile]);
    adbTry(['logcat','-c']);
    if (input !== null) adb(['shell','run-as',packageName,'tee',inputFile], { input });
    const launchArgs = ['shell','am','start','-n',`${packageName}/${activityName}`,'--es','mode',mode === 'produce' ? 'layout-produce' : 'layout-verify','--es','commit',commit,'--es','sdk',sdk,'--es','runtimeFamily',runtimeFamily];
    if (profile) launchArgs.push('--es','profile',profile);
    const launchOutput = adb(launchArgs);
    try {
        const parsed = JSON.parse(await waitForResult(launchOutput));
        if (parsed?.portableProbeError) throw new Error(parsed.portableProbeError);
        if (mode === 'produce') {
            await writeLayoutCorpus(parsed, outputPath);
            console.log(`Android layout producer wrote ${parsed.cases?.length ?? 0} ${parsed.profile} fixtures for ${parsed.runtime?.platformTag}.`);
        } else {
            await fs.mkdir(path.dirname(outputPath), { recursive: true });
            await fs.writeFile(outputPath, JSON.stringify(parsed, null, 2) + '\n', 'utf8');
            const incompatible = (parsed.results ?? []).filter(item => !item.rawWireCompatible).length;
            console.log(`Android layout consumer verified ${parsed.results?.length ?? 0} entries; observed incompatibilities: ${incompatible}.`);
        }
    } finally { try { adb(['shell','am','force-stop',packageName]); } catch {} }
}

const args = process.argv.slice(2);
if (args[0] === 'produce' && args.length === 6) {
    run('produce', null, args[1], args[2], args[3], args[4], args[5]).catch(error => { console.error(error.stack ?? error); process.exitCode = 1; });
} else if (args[0] === 'verify' && args.length === 6) {
    run('verify', args[1], args[2], null, args[3], args[4], args[5]).catch(error => { console.error(error.stack ?? error); process.exitCode = 1; });
} else {
    console.error('Usage: run-layout-android.mjs produce <corpus-output> <profile> <commit> <sdk> <runtime-family>');
    console.error('   or: run-layout-android.mjs verify <producer-root> <report-output> <commit> <sdk> <runtime-family>');
    process.exit(2);
}

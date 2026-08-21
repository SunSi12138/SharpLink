import fs from 'node:fs/promises';
import path from 'node:path';
import { spawnSync } from 'node:child_process';
import { loadEnvelopes, writeCorpus } from '../SharpLink.CodecCompatibility.Browser/portable-artifacts.mjs';

const packageName = 'com.sharplink.codeccompat';
const inputFile = 'files/sharplink-input.json';
const resultFile = 'files/sharplink-result.json';

function adb(args, options = {}) {
    const result = spawnSync('adb', args, { encoding: 'utf8', ...options });
    if (result.status !== 0) {
        throw new Error(`adb ${args.join(' ')} failed (${result.status}):\n${result.stdout ?? ''}\n${result.stderr ?? ''}`);
    }
    return result.stdout ?? '';
}

function adbTry(args, options = {}) {
    return spawnSync('adb', args, { encoding: 'utf8', ...options });
}

function delay(milliseconds) {
    return new Promise(resolve => setTimeout(resolve, milliseconds));
}

function collectDiagnostics(launchOutput) {
    const pid = adbTry(['shell', 'pidof', packageName]);
    const packageDump = adbTry(['shell', 'dumpsys', 'package', packageName]);
    const logcat = adbTry(['logcat', '-d', '-t', '2000']);
    const filteredLogcat = `${logcat.stdout ?? ''}\n${logcat.stderr ?? ''}`
        .split(/\r?\n/)
        .filter(line => /sharplink|codeccompat|androidruntime|mono|dotnet|system\.(invalidoperationexception|io\.)/i.test(line))
        .join('\n');
    const activityLines = `${packageDump.stdout ?? ''}`
        .split(/\r?\n/)
        .filter(line => /MainActivity|com\.sharplink\.codeccompat/i.test(line))
        .slice(0, 120)
        .join('\n');

    return [
        `am start output:\n${launchOutput}`,
        `pidof ${packageName}: ${pid.stdout?.trim() || '(none)'}\n${pid.stderr ?? ''}`,
        `package/activity excerpt:\n${activityLines}`,
        `filtered logcat:\n${filteredLogcat || '(no matching lines)'}`
    ].join('\n\n');
}

async function waitForResult(launchOutput) {
    const deadline = Date.now() + 120_000;
    while (Date.now() < deadline) {
        const exists = adbTry(['shell', 'run-as', packageName, 'test', '-f', resultFile]);
        if (exists.status === 0) {
            return adb(['shell', 'run-as', packageName, 'cat', resultFile]);
        }
        await delay(250);
    }
    throw new Error(`Android probe timed out waiting for app-private result file.\n${collectDiagnostics(launchOutput)}`);
}

async function runAndroid(mode, producerRoot, outputPath, commit, sdkVersion, runtimeFamily) {
    const input = mode === 'verify' ? JSON.stringify(await loadEnvelopes(producerRoot)) : null;

    adb(['shell', 'am', 'force-stop', packageName]);
    adb(['shell', 'run-as', packageName, 'mkdir', '-p', 'files']);
    adbTry(['shell', 'run-as', packageName, 'rm', '-f', inputFile, resultFile]);
    adbTry(['logcat', '-c']);

    if (input !== null) {
        adb(
            ['shell', 'run-as', packageName, 'sh', '-c', `cat > ${inputFile}`],
            { input });
    }

    const launchOutput = adb([
        'shell', 'am', 'start',
        '-n', `${packageName}/.MainActivity`,
        '--es', 'mode', mode,
        '--es', 'commit', commit,
        '--es', 'sdk', sdkVersion,
        '--es', 'runtimeFamily', runtimeFamily
    ]);
    console.log(`Android activity launch: ${launchOutput.trim()}`);

    try {
        const resultText = await waitForResult(launchOutput);
        const parsed = JSON.parse(resultText);
        if (parsed?.portableProbeError) {
            throw new Error(parsed.portableProbeError);
        }

        if (mode === 'produce') {
            await writeCorpus(parsed, outputPath);
            console.log(`Android producer wrote ${parsed.manifest?.cases?.length ?? 0} fixtures for ${parsed.manifest?.platformTag}.`);
        } else {
            await fs.mkdir(path.dirname(outputPath), { recursive: true });
            await fs.writeFile(outputPath, JSON.stringify(parsed, null, 2) + '\n', 'utf8');
            const blocking = (parsed.results ?? []).filter(item => item.blocking).length;
            console.log(`Android consumer verified ${parsed.results?.length ?? 0} entries; blocking failures: ${blocking}.`);
            if (blocking !== 0) process.exitCode = 1;
        }
    } finally {
        try { adb(['shell', 'am', 'force-stop', packageName]); } catch {}
    }
}

const args = process.argv.slice(2);
const mode = args[0];
if (mode === 'produce' && args.length === 5) {
    runAndroid('produce', null, args[1], args[2], args[3], args[4]).catch(error => {
        console.error(error.stack ?? error);
        process.exitCode = 1;
    });
} else if (mode === 'verify' && args.length === 6) {
    runAndroid('verify', args[1], args[2], args[3], args[4], args[5]).catch(error => {
        console.error(error.stack ?? error);
        process.exitCode = 1;
    });
} else {
    console.error('Usage: run-android.mjs produce <corpus-output> <commit> <sdk> <runtime-family>');
    console.error('   or: run-android.mjs verify <producer-root> <report-output> <commit> <sdk> <runtime-family>');
    process.exit(2);
}

import fs from 'node:fs/promises';
import { runCommand } from './ios-command.mjs';

try {
    const { stdout } = await runCommand('simulator-list', 'xcrun', ['simctl', 'list', 'devices', 'available', '-j'], { timeoutMs: 15_000 });
    const data = JSON.parse(stdout);
    let selected;
    for (const [runtime, devices] of Object.entries(data.devices)) {
        if (!runtime.includes('iOS')) continue;
        const device = devices.find(item => item.isAvailable && item.name?.startsWith('iPhone'));
        if (device) { selected = { ...device, runtime }; break; }
    }
    if (!selected || !/^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(selected.udid)) throw new Error('No available iPhone simulator found.');
    console.log(`Selected simulator: ${selected.name}, runtime=${selected.runtime}, udid=${selected.udid}, initialState=${selected.state}`);
    if (selected.state !== 'Shutdown') {
        await runCommand('simulator-shutdown', 'xcrun', ['simctl', 'shutdown', selected.udid], { timeoutMs: 30_000 });
    }
    await runCommand('simulator-boot', 'xcrun', ['simctl', 'boot', selected.udid], { timeoutMs: 90_000 });
    await runCommand('simulator-ready', 'xcrun', ['simctl', 'bootstatus', selected.udid, '-b'], { timeoutMs: 120_000 });
    if (!process.env.GITHUB_ENV) throw new Error('GITHUB_ENV is required to hand off the exact simulator.');
    await fs.appendFile(process.env.GITHUB_ENV, `IOS_SIMULATOR_UDID=${selected.udid}\n`);
} catch (error) {
    console.error(error.stack ?? error);
    process.exitCode = 1;
}

import FS from "fs";

// = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = =
/* * * * * * * * * * * * * * * * * * * * * * * * * * * * * * * * * * * * * * */
const matcher = /\[assembly: AssemblyVersion\("([0-9]+\.[0-9]+\.[0-9]+)/;

export function readVersion(contents: string): string {
    const arr = matcher.exec(contents);
    if (arr !== null && arr.length === 2) {
        return arr[1];
    }

    return "";
}

export function writeVersion(contents: string, version: string): string {
    return contents.replace(matcher, '[assembly: AssemblyVersion("' + version);
}

// = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = =
/* * * * * * * * * * * * * * * * * * * * * * * * * * * * * * * * * * * * * * */
/* istanbul ignore if */
if (require.main === module) {
    const args = process.argv.slice(2);

    if (args.length == 1) {
        process.stdout.write(
            readVersion(FS.readFileSync(args[0], "utf8")) + "\n"
        );
    } else if (args.length == 2) {
        FS.writeFileSync(
            args[0],
            writeVersion(FS.readFileSync(args[0], "utf8"), args[1]) + "\n"
        );
    }
}

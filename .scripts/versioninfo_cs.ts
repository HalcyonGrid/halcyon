import FS from "fs";

// = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = =
/* * * * * * * * * * * * * * * * * * * * * * * * * * * * * * * * * * * * * * */
const matcher = /\[assembly: AssemblyVersion\("([^"]+)"/;

export function readVersion(contents: string): string {
    const arr = matcher.exec(contents);
    if (arr !== null && arr.length === 2) {
        return arr[1];
    }

    return "";
}

export function writeVersion(): string {
    throw new Error("Changing the version of the CS file is not supported.");
}

// = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = =
/* * * * * * * * * * * * * * * * * * * * * * * * * * * * * * * * * * * * * * */
/* istanbul ignore if */
if (require.main === module) {
    const args = process.argv.slice(2);

    process.stdout.write(readVersion(FS.readFileSync(args[0], "utf8")) + "\n");
}

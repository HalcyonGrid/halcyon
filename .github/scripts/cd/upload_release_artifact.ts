import { promises as FS } from "fs";
import Path from "path";

import { context, getOctokit } from "@actions/github";

import { ReleaseInfo } from "./create_release_draft";

// = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = =
/* * * * * * * * * * * * * * * * * * * * * * * * * * * * * * * * * * * * * * */
export async function uploadReleaseArtifact(
    artifactPath: string,
    artifactMimeType: string,
    releaseInfo: ReleaseInfo,
    pat: string
): Promise<void> {
    const github = getOctokit(pat);

    const assets = await github.repos.listReleaseAssets({
        ...context.repo,
        release_id: releaseInfo.release.id,
    });

    const name = Path.basename(artifactPath);

    for (const asset of assets.data) {
        if (asset.name === name) {
            await github.repos.deleteReleaseAsset({
                ...context.repo,
                asset_id: asset.id,
            });
        }
    }

    await github.repos.uploadReleaseAsset({
        ...context.repo,
        // baseUrl: releaseInfo.release.uploadUrl,
        data: await FS.readFile(artifactPath, "binary"), // This is such a bad idea. Why didn't they utilize a file stream interface?!
        headers: {
            "content-length": (await FS.stat(artifactPath)).size,
            "content-type": artifactMimeType,
        },
        mediaType: {
            format: "raw",
        },
        name,
        release_id: releaseInfo.release.id,
    });
}

// = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = =
/* * * * * * * * * * * * * * * * * * * * * * * * * * * * * * * * * * * * * * */
/* istanbul ignore if */
if (require.main === module) {
    (async (): Promise<void> => {
        /* * * * * * * * * * * * * * * * * * * * * * * * * * * * * * * * * * * * * * */
        const args = process.argv.slice(2);

        if (args.length !== 3) {
            throw new Error(
                `Invalid number of arguments!\n${JSON.stringify({ args })}`
            );
        }

        const releaseInfo = process.env.RELEASE_INFO;

        if (!releaseInfo) {
            throw new Error(`Environment variable RELEASE_INFO not set!`);
        }

        return uploadReleaseArtifact(
            args[0],
            args[1],
            JSON.parse(releaseInfo),
            args[2]
        );
    })().catch((e) => {
        process.stderr.write(
            `::error ::${e.message
                .replace(/%/g, "%25")
                .replace(/\n/g, "%0A")
                .replace(/\r/g, "%0D")}%0A${e.stack
                .replace(/%/g, "%25")
                .replace(/\n/g, "%0A")
                .replace(/\r/g, "%0D")}\n`
        );
        process.exit(1);
    });
}

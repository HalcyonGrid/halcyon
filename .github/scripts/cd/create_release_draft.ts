import { promises as FS } from "fs";

import { Context } from "@actions/github/lib/context";
import { GitHub } from "@actions/github/lib/utils";
import { RequestParameters } from "@octokit/types";

// = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = =
/* * * * * * * * * * * * * * * * * * * * * * * * * * * * * * * * * * * * * * */
export interface ReleaseInfo {
    release: {
        id: number;
        uploadUrl: string;
    };
}

// = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = =
/* * * * * * * * * * * * * * * * * * * * * * * * * * * * * * * * * * * * * * */
type QueryVariables = RequestParameters & {
    owner: string;
    repo: string;
};

// = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = = =
/* * * * * * * * * * * * * * * * * * * * * * * * * * * * * * * * * * * * * * */
export async function createReleaseDraft(
    github: InstanceType<typeof GitHub>,
    context: Context,
    newVersion: string,
    changelogPath?: string
): Promise<ReleaseInfo> {
    const versionTag = `v${newVersion}`;

    const variables: QueryVariables = {
        owner: context.repo.owner,
        repo: context.repo.repo,
    };

    const { data: releases } = await github.repos.listReleases({
        ...variables,
    });

    let body: string | undefined;

    if (changelogPath) {
        try {
            body = (await FS.readFile(changelogPath, "utf8")).trim();
        } catch {
            // Huh, no text.
        }
    }

    const currentRelease = releases?.find((rel) => rel.tag_name === versionTag);

    if (!currentRelease) {
        process.stdout.write(
            `No matching release found, creating from scratch.\n`
        );

        const created = await github.repos.createRelease({
            ...variables,
            body,
            draft: true,
            name: `Release ${newVersion}`,
            prerelease: true,
            tag_name: versionTag,
            target_commitish: context.sha,
        });

        if (Math.floor(created.status / 100) !== 2) {
            process.stderr.write(
                `${JSON.stringify({
                    message: "Failure creating release.",
                    error: created,
                })}\n`
            );

            process.exit(1);
        }

        return {
            release: {
                id: created.data.id,
                uploadUrl: created.data.upload_url,
            },
        };
    }

    // Make sure that if we matched an existing version that is no longer a draft that it is the same SHA: aka a rerun of the workflow.
    if (
        !currentRelease.draft &&
        currentRelease.target_commitish !== context.sha
    ) {
        // Whoops, we are in a bad state: not sure how we could be here unless the surrounding tooling is violated the presumptions.
        process.stderr.write(
            `${JSON.stringify({
                message:
                    "Current release has been released, but is for a different commit!",
                error: { currentRelease, context },
            })}\n`
        );

        process.exit(1);
    }

    process.stdout.write(`Updating existing draft release.\n`);

    const updated = await github.repos.updateRelease({
        ...variables,
        body,
        release_id: currentRelease.id,
        tag_name: versionTag,
        target_commitish: context.sha,
    });

    if (Math.floor(updated.status / 100) !== 2) {
        process.stderr.write(
            `${JSON.stringify({
                message: "Failure updating release.",
                error: updated,
            })}\n`
        );

        process.exit(1);
    }

    return {
        release: { id: updated.data.id, uploadUrl: updated.data.upload_url },
    };
}

/* * * * * * * * * * * * * * * * * * * * * * * * * * * * * * * * * * * * * * */
export default createReleaseDraft;

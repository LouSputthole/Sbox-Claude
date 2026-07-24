import GithubSlugger, { slug as githubSlug } from "../vendor/github-slugger/index.mjs";

export function cleanHeadingTitle(value) {
  return value
    .replace(/\s+#+\s*$/, "")
    .replace(/!\[([^\]]*)\]\([^)]*\)/g, "$1")
    .replace(/\[([^\]]+)\]\([^)]*\)/g, "$1")
    .replace(/<[^>]+>/g, "")
    .replace(/[*_~]/g, "")
    .replace(/\x60/g, "")
    .trim();
}

export function slugBase(value) {
  return githubSlug(cleanHeadingTitle(value));
}

export function collectHeadings(text) {
  const headings = [];
  const slugger = new GithubSlugger();
  let fence = null;

  for (const line of text.replace(/\r\n/g, "\n").split("\n")) {
    const fenceMatch = line.match(/^\s*(\x60{3,}|~{3,})/);
    if (fenceMatch) {
      const marker = fenceMatch[1][0];
      fence = fence === null ? marker : (fence === marker ? null : fence);
      continue;
    }
    if (fence !== null) continue;

    const match = line.match(/^(#{1,6})\s+(.+?)\s*$/);
    if (!match) continue;
    const title = cleanHeadingTitle(match[2]);
    const slug = slugger.slug(title);
    if (!slug) continue;

    headings.push({
      depth: match[1].length,
      title,
      slug,
    });
  }

  return headings;
}

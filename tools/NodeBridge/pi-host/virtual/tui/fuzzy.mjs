// Pi b4afdddf44e059c67ccf5097e5317d913521b470 (MIT): packages/tui/src/fuzzy.ts.
// Ported to plain ESM for the PiSharp Node extension bridge (TypeScript types stripped mechanically; behaviour unchanged).
function scorePositions(textLower, positions) {
    let score = 0;
    let lastMatchIndex = -1;
    let consecutiveMatches = 0;
    for (const i of positions){
        if (lastMatchIndex === i - 1) {
            consecutiveMatches++;
            score -= consecutiveMatches * 5;
        } else {
            consecutiveMatches = 0;
            if (lastMatchIndex >= 0) {
                score += (i - lastMatchIndex - 1) * 2;
            }
        }
        if (i === 0 || /[\s\-_./:]/.test(textLower[i - 1])) {
            score -= 10;
        }
        score += i * 0.1;
        lastMatchIndex = i;
    }
    return score;
}
function matchQuery(normalizedQuery, textLower) {
    if (normalizedQuery.length === 0) {
        return {
            matches: true,
            score: 0
        };
    }
    if (normalizedQuery.length > textLower.length) {
        return {
            matches: false,
            score: 0
        };
    }
    const positions = [];
    let lastMatchIndex = -1;
    for(let queryIndex = 0; queryIndex < normalizedQuery.length; queryIndex++){
        const i = textLower.indexOf(normalizedQuery[queryIndex], lastMatchIndex + 1);
        if (i === -1) {
            return {
                matches: false,
                score: 0
            };
        }
        positions.push(i);
        lastMatchIndex = i;
    }
    let score = scorePositions(textLower, positions);
    for(let start = textLower.indexOf(normalizedQuery); start !== -1; start = textLower.indexOf(normalizedQuery, start + 1)){
        for(let k = 0; k < positions.length; k++){
            positions[k] = start + k;
        }
        score = Math.min(score, scorePositions(textLower, positions));
    }
    if (normalizedQuery === textLower) {
        score -= 100;
    }
    return {
        matches: true,
        score
    };
}
export function fuzzyMatch(query, text) {
    const queryLower = query.toLowerCase();
    const textLower = text.toLowerCase();
    const primaryMatch = matchQuery(queryLower, textLower);
    if (primaryMatch.matches) {
        return primaryMatch;
    }
    const alphaNumericMatch = queryLower.match(/^(?<letters>[a-z]+)(?<digits>[0-9]+)$/);
    const numericAlphaMatch = queryLower.match(/^(?<digits>[0-9]+)(?<letters>[a-z]+)$/);
    const swappedQuery = alphaNumericMatch ? `${alphaNumericMatch.groups?.digits ?? ""}${alphaNumericMatch.groups?.letters ?? ""}` : numericAlphaMatch ? `${numericAlphaMatch.groups?.letters ?? ""}${numericAlphaMatch.groups?.digits ?? ""}` : "";
    if (!swappedQuery) {
        return primaryMatch;
    }
    const swappedMatch = matchQuery(swappedQuery, textLower);
    if (!swappedMatch.matches) {
        return primaryMatch;
    }
    return {
        matches: true,
        score: swappedMatch.score + 5
    };
}
export function fuzzyFilter(items, query, getText) {
    if (!query.trim()) {
        return items;
    }
    const tokens = query.trim().split(/[\s/]+/).filter((t)=>t.length > 0);
    if (tokens.length === 0) {
        return items;
    }
    const results = [];
    for (const item of items){
        const text = getText(item);
        let totalScore = 0;
        let allMatch = true;
        for (const token of tokens){
            const match = fuzzyMatch(token, text);
            if (match.matches) {
                totalScore += match.score;
            } else {
                allMatch = false;
                break;
            }
        }
        if (allMatch) {
            results.push({
                item,
                totalScore
            });
        }
    }
    results.sort((a, b)=>a.totalScore - b.totalScore);
    return results.map((r)=>r.item);
}

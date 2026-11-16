#!/usr/bin/env python3
"""
Offline compiler for LexShield Core declarative bundle.

Reads the frozen upstream snapshot of filter lists and resources plus Lex Junior's own
`tools/lex-extra-rules.txt` and outputs `LexBrowser/Assets/shield/lex-shield.lex`
(format LEXSHLD2).

Exclusions enforced at compile time:
  - YouTube & Dailymotion domains (strictly isolated in YouTubeOptimizer/MediaShieldRules
    and DailymotionOptimizer/DailymotionRules)
  - Full /regex/ network filters (unsupported in Phase 2-4 per design budget)
  - Out-of-scope options: $csp, $removeparam, $cname, $urlskip, $ipaddress, $header, $tag,
    $strict1p, $strict3p, $match-case
  - Unsupported cosmetic syntaxes: HTML filtering (##^), AdGuard (#%#, #$#), regex locations
  - $badfilter rules are canonicalized and cancel matching rules ahead of time

Usage:
  python tools/compile_lex_shield.py
"""

from __future__ import annotations

import base64
import json
import os
import re
import struct
import sys
import zlib
from collections import Counter, defaultdict

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
UPSTREAM_DIR = os.path.join(ROOT, "COMO BLOQUEA BRAVE")
UPSTREAM_DATA_DIR = os.path.join(UPSTREAM_DIR, "adblock-rust-master", "adblock-rust-master", "data", "brave")
EXTRA_RULES_PATH = os.path.join(ROOT, "tools", "lex-extra-rules.txt")
OUTPUT_PATH = os.path.join(ROOT, "LexBrowser", "Assets", "shield", "lex-shield.lex")

INPUT_LISTS = [
    os.path.join(UPSTREAM_DIR, "adblock-rust-master", "adblock-rust-master", "data", "brave", "brave-main-list.txt"),
    os.path.join(UPSTREAM_DIR, "adblock-rust-master", "adblock-rust-master", "data", "brave", "coin-miners.txt"),
    os.path.join(UPSTREAM_DIR, "adblock-rust-master", "adblock-rust-master", "data", "easylist.to", "easylist", "easylist.txt"),
    os.path.join(UPSTREAM_DIR, "adblock-rust-master", "adblock-rust-master", "data", "easylist.to", "easylist", "easyprivacy.txt"),
    os.path.join(UPSTREAM_DIR, "adblock-rust-master", "data", "easylist.to", "easylist", "easylist.txt"),
    os.path.join(UPSTREAM_DIR, "adblock-rust-master", "data", "easylist.to", "easylist", "easyprivacy.txt"),
    os.path.join(UPSTREAM_DIR, "adblock-rust-master", "data", "easylist.to", "easylistspanish", "easylistspanish.txt"),
    os.path.join(UPSTREAM_DIR, "adblock-rust-master", "adblock-rust-master", "data", "uBlockOrigin", "filters.txt"),
    os.path.join(UPSTREAM_DIR, "adblock-rust-master", "adblock-rust-master", "data", "uBlockOrigin", "badware.txt"),
    os.path.join(UPSTREAM_DIR, "adblock-rust-master", "adblock-rust-master", "data", "uBlockOrigin", "resource-abuse.txt"),
    os.path.join(UPSTREAM_DIR, "adblock-rust-master", "adblock-rust-master", "data", "uBlockOrigin", "unbreak.txt"),
    os.path.join(UPSTREAM_DIR, "adblock-lists-master", "brave-unbreak.txt"),
    os.path.join(UPSTREAM_DIR, "adblock-lists-master", "brave-lists", "brave-firstparty.txt"),
    os.path.join(UPSTREAM_DIR, "adblock-lists-master", "brave-lists", "brave-firstparty-regional.txt"),
    os.path.join(UPSTREAM_DIR, "adblock-lists-master", "brave-lists", "brave-cookie-specific.txt"),
    os.path.join(UPSTREAM_DIR, "adblock-lists-master", "brave-lists", "brave-specific.txt"),
    os.path.join(UPSTREAM_DIR, "adblock-lists-master", "brave-lists", "brave-sugarcoat.txt"),
]
RESOURCES_JSONS = [
    os.path.join(UPSTREAM_DIR, "adblock-rust-master", "adblock-rust-master", "data", "brave", "brave-resources.json"),
    os.path.join(UPSTREAM_DIR, "adblock-resources-master", "adblock-resources-master", "dist", "resources.json"),
]

YT_DM_DOMAINS = (
    "youtube.com",
    "youtu.be",
    "youtube-nocookie.com",
    "googlevideo.com",
    "ytimg.com",
    "dailymotion.com",
    "dmcdn.net",
    "dmxleo.com",
)

# Deterministic FNV-1a 64-bit matching C# LexShieldHash.Fnv1a64
FNV_OFFSET = 14695981039346656037
FNV_PRIME = 1099511628211


def fnv1a_64(text: str) -> int:
    h = FNV_OFFSET
    for b in text.encode("utf-8"):
        h ^= b
        h = (h * FNV_PRIME) & 0xFFFFFFFFFFFFFFFF
    return h


def fnv1a_64_token(text: str) -> int:
    # Tokens stored in bucket index use 64-bit hash too (avoiding 32-bit bucket collisions)
    return fnv1a_64(text)


def is_yt_dm_host(host: str) -> bool:
    h = host.lstrip("~").strip().rstrip(".").lower()
    if not h:
        return False
    if h.endswith(".*"):
        return h[:-2] in ("youtube", "youtu", "dailymotion", "dmcdn", "youtube-nocookie")
    return any(h == d or h.endswith("." + d) for d in YT_DM_DOMAINS)


# Bitflags mirroring LexShieldMask in C#
FROM_IMAGE = 1 << 0
FROM_MEDIA = 1 << 1
FROM_OBJECT = 1 << 2
FROM_OTHER = 1 << 3
FROM_PING = 1 << 4
FROM_SCRIPT = 1 << 5
FROM_STYLESHEET = 1 << 6
FROM_SUBDOCUMENT = 1 << 7
FROM_WEBSOCKET = 1 << 8
FROM_XMLHTTPREQUEST = 1 << 9
FROM_FONT = 1 << 10
FROM_HTTP = 1 << 11
FROM_HTTPS = 1 << 12
IS_IMPORTANT = 1 << 13
FROM_POPUP = 1 << 14
THIRD_PARTY = 1 << 16
FIRST_PARTY = 1 << 17
IS_REGEX = 1 << 18
IS_LEFT_ANCHOR = 1 << 19
IS_RIGHT_ANCHOR = 1 << 20
IS_HOSTNAME_ANCHOR = 1 << 21
IS_EXCEPTION = 1 << 22
IS_GENERIC_HIDE = 1 << 23
IS_REDIRECT = 1 << 24
IS_ALSO_BLOCK_REDIRECT = 1 << 25
FROM_GET = 1 << 26
FROM_HEAD = 1 << 27
IS_HOST_WILDCARD = 1 << 28
FROM_DOCUMENT = 1 << 29
FROM_POST = 1 << 30
IS_MATCH_CASE = 1 << 15
IS_FULL_REGEX = 1 << 31

FROM_ANY_METHODS = FROM_GET | FROM_HEAD | FROM_POST
FROM_NETWORK_TYPES = (
    FROM_IMAGE
    | FROM_MEDIA
    | FROM_OBJECT
    | FROM_OTHER
    | FROM_PING
    | FROM_SCRIPT
    | FROM_STYLESHEET
    | FROM_SUBDOCUMENT
    | FROM_WEBSOCKET
    | FROM_XMLHTTPREQUEST
    | FROM_FONT
)
FROM_ALL_TYPES = FROM_NETWORK_TYPES | FROM_DOCUMENT

CPT_OPT_MAP = {
    "image": FROM_IMAGE,
    "media": FROM_MEDIA,
    "object": FROM_OBJECT,
    "object-subrequest": FROM_OBJECT,
    "other": FROM_OTHER,
    "ping": FROM_PING,
    "beacon": FROM_PING,
    "script": FROM_SCRIPT,
    "stylesheet": FROM_STYLESHEET,
    "css": FROM_STYLESHEET,
    "subdocument": FROM_SUBDOCUMENT,
    "frame": FROM_SUBDOCUMENT,
    "websocket": FROM_WEBSOCKET,
    "xmlhttprequest": FROM_XMLHTTPREQUEST,
    "xhr": FROM_XMLHTTPREQUEST,
    "font": FROM_FONT,
    "document": FROM_DOCUMENT,
    "doc": FROM_DOCUMENT,
    "popup": FROM_POPUP,
}

METHOD_OPT_MAP = {
    "get": FROM_GET,
    "head": FROM_HEAD,
    "post": FROM_POST,
}

UNSUPPORTED_OPTS = {
    "removeparam",
    "cname",
    "urlskip",
    "ipaddress",
    "header",
    "strict1p",
    "strict3p",
    "empty",
    "mp4",
    "queryprune",
    "tag",
    "permissions",
}

COS_SEPS = ("#@?#", "#@$#", "#@%#", "#?#", "#$#", "#%#", "#@#", "##^", "##")


def find_cos_sep(line: str) -> tuple[int, str | None]:
    best_i = -1
    best_sep = None
    for sep in COS_SEPS:
        idx = line.find(sep)
        if idx != -1 and (
            best_i == -1
            or idx < best_i
            or (idx == best_i and len(sep) > len(best_sep or ""))
        ):
            best_i = idx
            best_sep = sep
    return best_i, best_sep


def tokenize_filter_part(pattern: str, skip_first_token: bool, skip_last_token: bool) -> list[str]:
    """Exact port of adblock-rust src/utils.rs::fast_tokenizer_no_regex."""
    tokens: list[str] = []
    inside = False
    start = 0
    preceding = None
    for i, c in enumerate(pattern):
        if c.isalnum():
            if not inside:
                inside = True
                start = i
        elif inside:
            inside = False
            if (
                (start != 0 or not skip_first_token)
                and (i - start > 1)
                and c != "*"
                and preceding != "*"
            ):
                tokens.append(pattern[start:i])
            preceding = c
        else:
            preceding = c
    if (
        not skip_last_token
        and inside
        and (len(pattern) - start > 1)
        and preceding != "*"
    ):
        tokens.append(pattern[start:])
    return tokens


def tokenize_plain(pattern: str) -> list[str]:
    return tokenize_filter_part(pattern, False, False)


_REGEX_TRIVIAL_LITERALS = {"http", "https", "http:", "https:", "http://", "https://", "://", "www.", ".com"}


def normalize_js_regex(raw_pat: str) -> str | None:
    if not raw_pat:
        return None
    out: list[str] = []
    i = 0
    n = len(raw_pat)
    while i < n:
        c = raw_pat[i]
        if c == "\\":
            if i + 1 >= n:
                return None
            nxt = raw_pat[i + 1]
            if nxt == "/":
                out.append("/")
                i += 2
                continue
            out.append(c)
            out.append(nxt)
            i += 2
            continue
        out.append(c)
        i += 1
    norm = "".join(out)
    # Reject named backreferences or unsupported inline modes only if Python re fails
    try:
        re.compile(norm)
    except re.error:
        return None
    return norm


def split_regex_rule(line: str) -> tuple[bool, str, str] | None:
    is_exc = line.startswith("@@/")
    if not is_exc and not line.startswith("/"):
        return None
    start = 3 if is_exc else 1
    escaped = False
    candidates: list[tuple[str, str]] = []
    for i in range(start, len(line)):
        ch = line[i]
        if escaped:
            escaped = False
            continue
        if ch == "\\":
            escaped = True
            continue
        if ch == "/":
            if i == len(line) - 1:
                candidates.append((line[start:i], ""))
            elif line[i + 1] == "$":
                candidates.append((line[start:i], line[i + 2 :]))
    for raw_pat, opt_str in candidates:
        norm = normalize_js_regex(raw_pat)
        if norm is not None:
            return (is_exc, norm, opt_str)
    return None


def extract_regex_prefilter(pattern: str) -> str:
    """
    Extract a guaranteed-present lowercase ASCII literal substring (>= 4 chars)
    for O(1) Contains prefiltering before Regex.IsMatch.
    Returns '' whenever top-level '|' alternation exists or no safe literal >= 4 chars is found.
    """
    runs: list[str] = []
    cur: list[str] = []
    parens = 0
    brackets = 0
    escaped = False
    i = 0
    n = len(pattern)
    meta = set(".^$+*?{}()[]|\\")
    esc_literal = set("/.-_:?&=+*$^~@!#%")

    def flush():
        if cur:
            runs.append("".join(cur))
            cur.clear()

    while i < n:
        c = pattern[i]
        if escaped:
            escaped = False
            if parens == 0 and brackets == 0:
                if c in esc_literal:
                    cur.append(c)
                else:
                    flush()
            i += 1
            continue

        if c == "\\":
            escaped = True
            i += 1
            continue

        if brackets > 0:
            if c == "]":
                brackets = 0
            i += 1
            continue

        if c == "[":
            flush()
            brackets = 1
            i += 1
            continue

        if c == "(":
            flush()
            parens += 1
            i += 1
            continue

        if c == ")":
            flush()
            if parens > 0:
                parens -= 1
            i += 1
            continue

        if parens > 0:
            i += 1
            continue

        if c == "|":
            # Top-level alternation -> no single literal is guaranteed
            return ""

        if c in ("?", "*"):
            if cur:
                cur.pop()
            flush()
            i += 1
            continue

        if c == "{":
            # Check {0,...} which makes the preceding token optional
            close_brace = pattern.find("}", i + 1)
            if close_brace != -1:
                inner = pattern[i + 1 : close_brace]
                if inner.startswith("0") or inner.startswith(","):
                    if cur:
                        cur.pop()
                flush()
                i = close_brace + 1
                continue
            flush()
            i += 1
            continue

        if c in meta:
            flush()
            i += 1
            continue

        if 32 <= ord(c) < 127:
            cur.append(c)
        else:
            flush()
        i += 1

    flush()

    candidates = []
    for r in runs:
        low = r.lower()
        if len(low) >= 4 and low not in _REGEX_TRIVIAL_LITERALS:
            candidates.append(low)
    if not candidates:
        return ""
    return max(candidates, key=lambda s: (len(s), "." in s or "/" in s))


def parse_network_rule(line: str, stats: Counter):
    is_full_regex = False
    is_regex_exc = False
    norm_regex = ""
    opt_str = ""
    pat_raw = line

    regex_split = split_regex_rule(line)
    if regex_split is not None:
        is_regex_exc, norm_regex, opt_str = regex_split
        is_full_regex = True
        if opt_str:
            if "urlskip=/" in opt_str or "replace=/" in opt_str or "uritransform=/" in opt_str:
                stats["net_unsupported_regex_modifier_skipped"] += 1
                return None
            opts = [o.strip() for o in opt_str.split(",") if o.strip()]
        else:
            opts = []
    else:
        if line.startswith("/") and (line.endswith("/") or "/$" in line):
            stats["net_regex_invalid_skipped"] += 1
            return None
        if line.startswith("@@/") and (line.endswith("/") or "/$" in line):
            stats["net_regex_invalid_skipped"] += 1
            return None

        dollar_idx = line.rfind("$")
        if dollar_idx != -1:
            pat_raw = line[:dollar_idx]
            opt_str = line[dollar_idx + 1 :]
            if "urlskip=/" in opt_str or "replace=/" in opt_str or "uritransform=/" in opt_str:
                stats["net_unsupported_regex_modifier_skipped"] += 1
                return None
            opts = [o.strip() for o in opt_str.split(",") if o.strip()]
        else:
            pat_raw = line
            opts = []

        if pat_raw.startswith("/") and pat_raw.endswith("/") and len(pat_raw) > 1:
            stats["net_regex_invalid_skipped"] += 1
            return None
        if pat_raw.startswith("@@/") and pat_raw.endswith("/") and len(pat_raw) > 3:
            stats["net_regex_invalid_skipped"] += 1
            return None

    mask = THIRD_PARTY | FIRST_PARTY | FROM_HTTP | FROM_HTTPS
    cpt_pos = 0
    cpt_neg = 0
    meth_pos = 0
    meth_neg = 0
    inc_doms: list[str] = []
    exc_doms: list[str] = []
    inc_to: list[str] = []
    exc_to: list[str] = []
    modifier = ""
    redirect_priority = 0
    is_badfilter = False
    is_csp = False
    is_match_case = False

    if is_full_regex:
        if is_regex_exc:
            mask |= IS_EXCEPTION
    elif pat_raw.startswith("@@"):
        mask |= IS_EXCEPTION
        pat_raw = pat_raw[2:]

    for o in opts:
        if "=" in o:
            k, v = o.split("=", 1)
            k = k.strip().lower()
            v = v.strip()
            if k in ("domain", "from"):
                for dv in v.split("|"):
                    dv = dv.strip().lower().rstrip(".")
                    if not dv:
                        continue
                    if dv.startswith("~"):
                        exc_doms.append(dv[1:])
                    else:
                        inc_doms.append(dv)
            elif k == "to":
                for dv in v.split("|"):
                    dv = dv.strip().lower().rstrip(".")
                    if not dv:
                        continue
                    if dv.startswith("~"):
                        exc_to.append(dv[1:])
                    else:
                        inc_to.append(dv)
            elif k == "denyallow":
                for dv in v.split("|"):
                    dv = dv.strip().lower().rstrip(".")
                    if dv:
                        exc_to.append(dv.lstrip("~"))
            elif k in ("redirect", "rewrite", "redirect-rule"):
                if not v or is_csp:
                    stats["net_invalid_redirect_skipped"] += 1
                    return None
                mask |= IS_REDIRECT
                if k != "redirect-rule":
                    mask |= IS_ALSO_BLOCK_REDIRECT
                # Extract optional :priority suffix
                if ":" in v:
                    r_base, r_prio = v.rsplit(":", 1)
                    try:
                        redirect_priority = max(-32768, min(32767, int(r_base_prio := int(r_prio))))
                        v = r_base
                    except ValueError:
                        pass
                modifier = v.strip()
            elif k == "csp":
                if not v or (mask & IS_REDIRECT):
                    stats["net_invalid_csp_skipped"] += 1
                    return None
                is_csp = True
                modifier = v
            elif k == "method":
                for mv in v.split("|"):
                    mv = mv.strip().lower()
                    neg_m = mv.startswith("~")
                    m_name = mv[1:] if neg_m else mv
                    bit = METHOD_OPT_MAP.get(m_name)
                    if not bit:
                        stats["net_unsupported_skipped"] += 1
                        return None
                    if neg_m:
                        meth_neg |= bit
                    else:
                        meth_pos |= bit
            else:
                stats[f"opt_{k}_skipped"] += 1
                return None
        else:
            neg = o.startswith("~")
            k = (o[1:] if neg else o).lower()
            if k == "badfilter":
                if neg:
                    return None
                is_badfilter = True
            elif k in UNSUPPORTED_OPTS:
                stats[f"opt_{k}_skipped"] += 1
                return None
            elif k == "important":
                if neg:
                    return None
                mask |= IS_IMPORTANT
            elif k in ("third-party", "3p"):
                if neg:
                    mask &= ~THIRD_PARTY
                else:
                    mask &= ~FIRST_PARTY
            elif k in ("first-party", "1p"):
                if neg:
                    mask &= ~FIRST_PARTY
                else:
                    mask &= ~THIRD_PARTY
            elif k in ("generichide", "ghide", "ehide", "elemhide", "shide", "specifichide"):
                if neg:
                    return None
                if k in ("generichide", "ghide", "ehide", "elemhide"):
                    mask |= IS_GENERIC_HIDE
                else:
                    stats[f"opt_{k}_skipped"] += 1
                    return None
            elif k in ("redirect", "rewrite", "redirect-rule") and (mask & IS_EXCEPTION) and not neg:
                if is_csp:
                    return None
                mask |= IS_REDIRECT
                if k != "redirect-rule":
                    mask |= IS_ALSO_BLOCK_REDIRECT
                modifier = ""
            elif k == "csp" and not neg and (mask & IS_EXCEPTION) and not (mask & IS_REDIRECT):
                is_csp = True
                modifier = ""
            elif k == "match-case" and not neg:
                is_match_case = True
            elif k == "popunder" and not neg:
                cpt_pos |= FROM_POPUP
            elif k == "all":
                if neg:
                    return None
                cpt_pos |= FROM_ALL_TYPES
            elif k in CPT_OPT_MAP:
                bit = CPT_OPT_MAP[k]
                if neg:
                    cpt_neg |= bit
                else:
                    cpt_pos |= bit
            else:
                stats[f"opt_unknown_{k}_skipped"] += 1
                return None

    if (mask & IS_GENERIC_HIDE) and not (mask & IS_EXCEPTION):
        stats["net_invalid_ghide"] += 1
        return None

    if is_match_case:
        if is_full_regex:
            mask |= IS_MATCH_CASE
        else:
            stats["opt_match-case_skipped"] += 1
            return None

    if is_csp:
        # CSP rules apply to document and subdocument unless narrowed by options
        if (cpt_pos & (FROM_DOCUMENT | FROM_SUBDOCUMENT | FROM_ALL_TYPES)) == 0:
            mask |= (cpt_pos | FROM_DOCUMENT | FROM_SUBDOCUMENT)
        else:
            mask |= cpt_pos
    else:
        mask |= cpt_pos
        if (cpt_neg & FROM_NETWORK_TYPES) != 0:
            mask |= FROM_NETWORK_TYPES
        if (cpt_pos & FROM_ALL_TYPES) == 0 and (cpt_pos & FROM_POPUP) == 0:
            mask |= FROM_NETWORK_TYPES

    # Methods mask resolution
    if meth_pos != 0 or meth_neg != 0:
        mask |= meth_pos
        if meth_neg != 0 and meth_pos == 0:
            mask |= FROM_ANY_METHODS
        mask &= ~meth_neg

    host_str = ""
    pat_str = ""
    if is_full_regex:
        mask |= IS_FULL_REGEX | IS_REGEX
        mask &= ~cpt_neg
        pat_str = norm_regex
        host_str = extract_regex_prefilter(norm_regex)
        if host_str:
            h_clean = host_str.strip("./")
            if is_yt_dm_host(h_clean):
                stats["net_ytdm_skipped"] += 1
                return None
    else:
        # Anchors
        p = pat_raw
        if p.startswith("||"):
            mask |= IS_HOSTNAME_ANCHOR
            p = p[2:]
        elif p.startswith("|"):
            mask |= IS_LEFT_ANCHOR
            p = p[1:]

        end_url_anchor = False
        if p.endswith("|"):
            mask |= IS_RIGHT_ANCHOR
            end_url_anchor = True
            p = p[:-1]

        pat_str = p
        if mask & IS_HOSTNAME_ANCHOR:
            m = re.search(r"[/^*]", p)
            if m:
                pos = m.start()
                host_str = p[:pos].lower()
                rest = p[pos:]
                if p[pos] == "*":
                    mask |= IS_HOST_WILDCARD
                if rest == "^":
                    pat_str = ""
                    mask |= IS_RIGHT_ANCHOR
                    end_url_anchor = False
                else:
                    pat_str = rest
                    mask |= IS_LEFT_ANCHOR
            else:
                host_str = p.lower()
                pat_str = ""

        if pat_str.endswith("*"):
            pat_str = pat_str[:-1]
        if not (mask & IS_HOSTNAME_ANCHOR) and pat_str.startswith("*"):
            mask &= ~IS_LEFT_ANCHOR
            pat_str = pat_str[1:]

        # Protocol filters http:// https:// ws://
        if mask & IS_LEFT_ANCHOR:
            if pat_str == "ws://":
                mask = (mask | FROM_WEBSOCKET) & ~(FROM_HTTP | FROM_HTTPS | IS_LEFT_ANCHOR)
                pat_str = ""
            elif pat_str == "http://":
                mask = (mask | FROM_HTTP) & ~(FROM_HTTPS | IS_LEFT_ANCHOR)
                pat_str = ""
            elif pat_str == "https://":
                mask = (mask | FROM_HTTPS) & ~(FROM_HTTP | IS_LEFT_ANCHOR)
                pat_str = ""
            elif pat_str == "http*://":
                mask = (mask | FROM_HTTP | FROM_HTTPS) & ~IS_LEFT_ANCHOR
                pat_str = ""

        if (
            (cpt_pos & FROM_ALL_TYPES) == 0
            and (cpt_pos & FROM_POPUP) == 0
            and (cpt_neg & FROM_ALL_TYPES) == 0
            and (mask & IS_HOSTNAME_ANCHOR)
            and (mask & IS_RIGHT_ANCHOR)
            and not end_url_anchor
        ):
            mask |= FROM_ALL_TYPES

        mask &= ~cpt_neg
        pat_str = pat_str.lower()
        if pat_str and ("*" in pat_str or "^" in pat_str):
            mask |= IS_REGEX

        # YT/DM isolation filtering
        if host_str and is_yt_dm_host(host_str):
            stats["net_ytdm_skipped"] += 1
            return None

    if inc_doms:
        filtered_inc = [d for d in inc_doms if not is_yt_dm_host(d)]
        if not filtered_inc:
            stats["net_ytdm_skipped"] += 1
            return None
        inc_doms = filtered_inc

    inc_doms = sorted(set(inc_doms))
    exc_doms = sorted(set(exc_doms))
    inc_to = sorted(set(inc_to))
    exc_to = sorted(set(exc_to))

    canon_key = (
        is_csp,
        is_full_regex,
        mask,
        host_str,
        pat_str,
        modifier,
        redirect_priority,
        tuple(inc_doms),
        tuple(exc_doms),
        tuple(inc_to),
        tuple(exc_to),
    )

    return {
        "is_badfilter": is_badfilter,
        "is_csp": is_csp,
        "is_full_regex": is_full_regex,
        "canon_key": canon_key,
        "mask": mask,
        "host": host_str,
        "pat": pat_str,
        "modifier": modifier,
        "prio": redirect_priority,
        "inc_doms": inc_doms,
        "exc_doms": exc_doms,
        "inc_to": inc_to,
        "exc_to": exc_to,
    }


# Upstream exceptions that conflict with higher-priority redirect rules
SUPPRESSED_RULES = {
    "@@||imasdk.googleapis.com/js/sdkloader/ima3.js$script,domain=viki.com",
}

def main() -> int:
    stats = Counter()
    raw_lines: list[str] = []

    seen_raw: set[str] = set()
    all_input_lists = list(INPUT_LISTS) + ([EXTRA_RULES_PATH] if os.path.exists(EXTRA_RULES_PATH) else [])
    for fn in all_input_lists:
        path = os.path.normpath(fn) if os.path.isabs(fn) else os.path.normpath(os.path.join(UPSTREAM_DATA_DIR, fn))
        if not os.path.exists(path):
            print(f"ERROR: Missing input file: {path}", file=sys.stderr)
            return 1
        with open(path, encoding="utf-8") as f:
            for raw in f:
                s = raw.strip()
                if not s or s.startswith("!") or s.startswith("["):
                    continue
                if s in SUPPRESSED_RULES:
                    stats["suppressed_override_rules"] += 1
                    continue
                if s not in seen_raw:
                    seen_raw.add(s)
                    raw_lines.append(s)

    parsed_net = []
    badfilter_keys = set()

    # Cosmetics accumulators (with payload deduplication)
    simple_classes: set[str] = set()
    simple_ids: set[str] = set()
    complex_class_map: dict[str, list[str]] = defaultdict(list)
    complex_id_map: dict[str, list[str]] = defaultdict(list)
    misc_generic_hide: list[str] = []

    # Payload records: (kind_code, content_str, not_hosts_tuple, perm_bit)
    # kind: 0 = CSS hide selector, 1 = CSS unhide, 2 = scriptlet (+js args), 3 = procedural/action
    payload_to_id: dict[tuple, int] = {}
    payloads: list[tuple[int, str, tuple[str, ...], int]] = []
    host_payload_ids: dict[str, list[int]] = defaultdict(list)
    host_uninject_all: set[str] = set()
    host_uninject_list: dict[str, set[str]] = defaultdict(set)

    def intern_payload(kind: int, text: str, neg_hosts: tuple[str, ...], perm: int = 0) -> int:
        k = (kind, text, neg_hosts, perm)
        pid = payload_to_id.get(k)
        if pid is None:
            pid = len(payloads)
            payloads.append(k)
            payload_to_id[k] = pid
        return pid

    for s in raw_lines:
        ci, sep = find_cos_sep(s)
        if ci != -1 and sep is not None:
            if sep in ("##^", "#%#", "#@%#", "#$#", "#@$#"):
                stats["cos_unsupported_skipped"] += 1
                continue
            dom_part = s[:ci].strip()
            body = s[ci + len(sep) :].strip()
            if not body:
                continue

            if not dom_part:
                if sep == "##":
                    if (
                        body.startswith("+js(")
                        or ":style(" in body
                        or ":remove(" in body
                        or ":has-text(" in body
                        or ":upward(" in body
                        or ":xpath(" in body
                    ):
                        stats["cos_generic_action_skipped"] += 1
                    elif body.startswith(".") and all(
                        c.isalnum() or c in "-_" for c in body[1:]
                    ):
                        simple_classes.add(body[1:])
                    elif body.startswith("#") and all(
                        c.isalnum() or c in "-_" for c in body[1:]
                    ):
                        simple_ids.add(body[1:])
                    else:
                        m_cls = re.match(r"^\.([A-Za-z0-9_-]+)[\s>+~.:#\[]", body)
                        m_id = re.match(r"^#([A-Za-z0-9_-]+)[\s>+~.:#\[]", body)
                        if m_cls:
                            complex_class_map[m_cls.group(1)].append(body)
                        elif m_id:
                            complex_id_map[m_id.group(1)].append(body)
                        else:
                            misc_generic_hide.append(body)
                continue

            doms = [d.strip().lower().rstrip(".") for d in dom_part.split(",") if d.strip()]
            pos_doms = [d for d in doms if not d.startswith("~") and not d.startswith("/") and not is_yt_dm_host(d)]
            neg_doms = tuple(sorted(d[1:] for d in doms if d.startswith("~") and not d[1:].startswith("/") and not is_yt_dm_host(d[1:])))
            if not pos_doms:
                if doms and all(is_yt_dm_host(d) for d in doms):
                    stats["cos_ytdm_skipped"] += 1
                else:
                    stats["cos_neg_only_or_regex_skipped"] += 1
                continue

            if sep == "#@#":
                if body.startswith("+js(") and body.endswith(")"):
                    inner = body[4:-1].strip()
                    for d in pos_doms:
                        if not inner:
                            host_uninject_all.add(d)
                        else:
                            host_uninject_list[d].add(inner)
                else:
                    pid = intern_payload(1, body, (), 0)
                    for d in pos_doms:
                        host_payload_ids[d].append(pid)
                continue

            # sep in ("##", "#?#")
            if body.startswith("+js(") and body.endswith(")"):
                inner = body[4:-1].strip()
                first_arg = inner.split(",", 1)[0].strip().strip("'\"`")
                perm = 1 if first_arg.startswith("trusted-") else 0
                pid = intern_payload(2, inner, neg_doms, perm)
                for d in pos_doms:
                    host_payload_ids[d].append(pid)
            elif any(
                op in body
                for op in (
                    ":style(",
                    ":remove(",
                    ":remove-attr(",
                    ":remove-class(",
                    ":has-text(",
                    ":upward(",
                    ":xpath(",
                    ":matches-css",
                    ":matches-attr(",
                    ":matches-path(",
                    ":min-text-length(",
                )
            ):
                pid = intern_payload(3, body, neg_doms, 0)
                for d in pos_doms:
                    host_payload_ids[d].append(pid)
            else:
                pid = intern_payload(0, body, neg_doms, 0)
                for d in pos_doms:
                    host_payload_ids[d].append(pid)
            continue

        # Network rule
        nr = parse_network_rule(s, stats)
        if nr is None:
            continue
        if nr["is_badfilter"]:
            badfilter_keys.add(nr["canon_key"])
            stats["net_badfilter_rules"] += 1
        else:
            parsed_net.append(nr)

    # Cancel rules matched by $badfilter
    active_net = []
    for nr in parsed_net:
        if nr["canon_key"] in badfilter_keys:
            stats["net_canceled_by_badfilter"] += 1
            continue
        active_net.append(nr)

    csp_rules = [nr for nr in active_net if nr["is_csp"]]
    regex_rules = [nr for nr in active_net if nr["is_full_regex"] and not nr["is_csp"]]
    normal_net = [nr for nr in active_net if not nr["is_csp"] and not nr["is_full_regex"]]

    # Split PureHost vs PatternRules (checking 64-bit hash collisions too)
    pure_host_map: dict[int, tuple[str, int]] = {}
    pure_multi_hashes: set[int] = set()
    pattern_rules = []
    seen_strings_by_hash: dict[int, str] = {}

    def verify_no_hash_collision(text: str) -> int:
        h = fnv1a_64(text)
        prev = seen_strings_by_hash.get(h)
        if prev is not None and prev != text:
            raise RuntimeError(f"64-bit FNV-1a collision detected: {prev!r} vs {text!r}")
        seen_strings_by_hash[h] = text
        return h

    for nr in normal_net:
        m = nr["mask"]
        is_pure = (
            (m & IS_HOSTNAME_ANCHOR) != 0
            and (m & IS_RIGHT_ANCHOR) != 0
            and (m & (IS_EXCEPTION | IS_IMPORTANT | IS_GENERIC_HIDE | IS_REDIRECT | IS_HOST_WILDCARD | IS_REGEX | FROM_ANY_METHODS | FROM_POPUP)) == 0
            and nr["pat"] == ""
            and nr["host"] != ""
            and not nr["inc_doms"]
            and not nr["exc_doms"]
            and not nr["inc_to"]
            and not nr["exc_to"]
        )
        if is_pure:
            h = verify_no_hash_collision(nr["host"])
            if h in pure_multi_hashes:
                pattern_rules.append(nr)
            elif h in pure_host_map:
                prev_host, prev_mask = pure_host_map.pop(h)
                pure_multi_hashes.add(h)
                pattern_rules.append({
                    "mask": prev_mask,
                    "host": prev_host,
                    "pat": "",
                    "modifier": "",
                    "prio": 0,
                    "inc_doms": [],
                    "exc_doms": [],
                    "inc_to": [],
                    "exc_to": [],
                })
                pattern_rules.append(nr)
            else:
                pure_host_map[h] = (nr["host"], m)
        else:
            pattern_rules.append(nr)

    # Verify zero collisions on cosmetic hostnames + simple classes/ids
    for sc in simple_classes:
        verify_no_hash_collision("." + sc)
    for si in simple_ids:
        verify_no_hash_collision("#" + si)
    for ch in set(host_payload_ids.keys()) | host_uninject_all | set(host_uninject_list.keys()):
        verify_no_hash_collision("C:" + ch)

    # Build packed PatternRules structs, StrBlob, HashPool, and rarest-token index
    str_blob = bytearray()
    str_cache: dict[str, tuple[int, int]] = {}

    def intern_str(s: str) -> tuple[int, int]:
        if not s:
            return (0, 0)
        cached = str_cache.get(s)
        if cached is not None:
            return cached
        b = s.encode("utf-8")
        off = len(str_blob)
        str_blob.extend(b)
        val = (off, len(b))
        str_cache[s] = val
        return val

    hash_pool: list[int] = []
    hash_pool_cache: dict[tuple[str, ...], tuple[int, int]] = {}

    def intern_doms(doms: list[str]) -> tuple[int, int]:
        if not doms:
            return (0, 0)
        key = tuple(doms)
        cached = hash_pool_cache.get(key)
        if cached is not None:
            return cached
        off = len(hash_pool)
        for d in doms:
            hash_pool.append(fnv1a_64(d))
        val = (off, len(doms))
        hash_pool_cache[key] = val
        return val

    # Pass 1: collect candidate tokens and global token frequencies across PatternRules
    cand_tokens_per_rule: list[list[str]] = []
    token_freq: Counter[str] = Counter()

    for nr in pattern_rules:
        m = nr["mask"]
        toks: list[str] = []
        if nr["pat"]:
            skip_first = (m & IS_RIGHT_ANCHOR) != 0 and (m & IS_HOSTNAME_ANCHOR) == 0
            skip_last = (m & IS_RIGHT_ANCHOR) == 0
            toks.extend(tokenize_filter_part(nr["pat"], skip_first, skip_last))
        if nr["host"]:
            if not (m & IS_HOST_WILDCARD):
                toks.extend(tokenize_plain(nr["host"]))
            else:
                last_dot = nr["host"].rfind(".")
                if last_dot != -1:
                    toks.extend(tokenize_plain(nr["host"][:last_dot]))
        # Also include protocol hint ONLY as fallback if empty
        dedup_toks = list(dict.fromkeys(toks))
        cand_tokens_per_rule.append(dedup_toks)
        for t in dedup_toks:
            token_freq[t] += 1

    # Pass 2: assign each PatternRule to either:
    #   - opt_domain_buckets (when no tokens AND 0 < len(inc_doms) <= 64, OR single inc_dom with no exc_doms!)
    #   - token_buckets (choosing RAREST candidate token!)
    #   - catch-all fallback list (when no safe token and no opt_domains)
    opt_dom_buckets: dict[int, list[int]] = defaultdict(list)
    tok_buckets: dict[int, list[int]] = defaultdict(list)
    fallback_rules: list[int] = []

    # Clean exact PatRec struct layout (56 bytes, natural 4-byte alignment):
    # u32 Mask
    # i32 HostOff, i32 HostLen, i32 PatOff, i32 PatLen, i32 ModOff, i32 ModLen
    # i32 IncDomOff, i32 ExcDomOff, i32 IncToOff, i32 ExcToOff
    # u16 IncDomLen, u16 ExcDomLen, u16 IncToLen, u16 ExcToLen
    # i16 RedirectPriority, u16 Reserved
    def pack_pat_recs(rules: list[dict]) -> bytearray:
        buf = bytearray()
        for nr in rules:
            h_off, h_len = intern_str(nr["host"])
            p_off, p_len = intern_str(nr["pat"])
            m_off, m_len = intern_str(nr["modifier"])
            id_off, id_len = intern_doms(nr["inc_doms"])
            ed_off, ed_len = intern_doms(nr["exc_doms"])
            it_off, it_len = intern_doms(nr["inc_to"])
            et_off, et_len = intern_doms(nr["exc_to"])

            if any(x > 65535 for x in (id_len, ed_len, it_len, et_len)):
                raise RuntimeError(f"Domain option list exceeded 65535 items: {(id_len, ed_len, it_len, et_len)}")

            buf.extend(
                struct.pack(
                    "<I10i4HhH",
                    nr["mask"] & 0xFFFFFFFF,
                    h_off,
                    h_len,
                    p_off,
                    p_len,
                    m_off,
                    m_len,
                    id_off,
                    ed_off,
                    it_off,
                    et_off,
                    id_len,
                    ed_len,
                    it_len,
                    et_len,
                    nr["prio"],
                    0,
                )
            )
        return buf

    pat_recs = pack_pat_recs(pattern_rules)
    csp_recs = pack_pat_recs(csp_rules)
    regex_recs = pack_pat_recs(regex_rules)

    for ridx, nr in enumerate(pattern_rules):
        # Bucket assignment
        toks = cand_tokens_per_rule[ridx]
        if len(nr["inc_doms"]) == 1 and not nr["exc_doms"]:
            opt_dom_buckets[fnv1a_64(nr["inc_doms"][0])].append(ridx)
        elif toks:
            best_tok = min(toks, key=lambda t: (token_freq[t], -len(t)))
            tok_buckets[fnv1a_64_token(best_tok)].append(ridx)
        elif 0 < len(nr["inc_doms"]) <= 512:
            for d in nr["inc_doms"]:
                opt_dom_buckets[fnv1a_64(d)].append(ridx)
        else:
            fallback_rules.append(ridx)

    # Flatten PureHosts into parallel sorted ulong[] + uint[]
    sorted_pure = sorted(pure_host_map.items(), key=lambda x: x[0])
    pure_hashes_bytes = struct.pack(f"<{len(sorted_pure)}Q", *(h for h, _ in sorted_pure)) if sorted_pure else b""
    pure_masks_bytes = struct.pack(f"<{len(sorted_pure)}I", *(m for _, (_, m) in sorted_pure)) if sorted_pure else b""

    def pack_bucket_map(bmap: dict[int, list[int]]):
        items = sorted(bmap.items(), key=lambda x: x[0])
        keys_b = struct.pack(f"<{len(items)}Q", *(k for k, _ in items)) if items else b""
        ranges_b = bytearray()
        vals = []
        off = 0
        for _, idxs in items:
            ranges_b.extend(struct.pack("<II", off, len(idxs)))
            vals.extend(idxs)
            off += len(idxs)
        vals_b = struct.pack(f"<{len(vals)}i", *vals) if vals else b""
        return keys_b, bytes(ranges_b), vals_b

    tok_keys_b, tok_ranges_b, tok_vals_b = pack_bucket_map(tok_buckets)
    opt_keys_b, opt_ranges_b, opt_vals_b = pack_bucket_map(opt_dom_buckets)
    fallback_b = struct.pack(f"<{len(fallback_rules)}i", *fallback_rules) if fallback_rules else b""
    hash_pool_b = struct.pack(f"<{len(hash_pool)}Q", *hash_pool) if hash_pool else b""

    # Cosmetics packing
    sc_hashes = sorted(fnv1a_64(c) for c in simple_classes)
    si_hashes = sorted(fnv1a_64(i) for i in simple_ids)
    sc_b = struct.pack(f"<{len(sc_hashes)}Q", *sc_hashes) if sc_hashes else b""
    si_b = struct.pack(f"<{len(si_hashes)}Q", *si_hashes) if si_hashes else b""

    # Cosmetic payloads & per-host slices
    payload_records = [
        {"k": k, "t": text, "nh": list(neg_hosts), "p": perm}
        for (k, text, neg_hosts, perm) in payloads
    ]
    all_cos_hosts = sorted(
        set(host_payload_ids.keys()) | host_uninject_all | set(host_uninject_list.keys()),
        key=lambda d: fnv1a_64(d),
    )
    cos_host_hashes_list = []
    cos_host_slices_b = bytearray()
    cos_payload_id_list = []
    uninject_host_meta = {}
    for d in all_cos_hosts:
        h = fnv1a_64(d)
        pids = host_payload_ids.get(d, [])
        off = len(cos_payload_id_list)
        cos_payload_id_list.extend(pids)
        flags = 1 if d in host_uninject_all else 0
        cos_host_hashes_list.append(h)
        cos_host_slices_b.extend(struct.pack("<IHBB", off, min(len(pids), 65535), flags, 0))
        if d in host_uninject_list:
            uninject_host_meta[d] = sorted(host_uninject_list[d])

    cos_hosts_hash_b = struct.pack(f"<{len(cos_host_hashes_list)}Q", *cos_host_hashes_list) if cos_host_hashes_list else b""
    cos_pid_list_b = struct.pack(f"<{len(cos_payload_id_list)}i", *cos_payload_id_list) if cos_payload_id_list else b""
    cos_payloads_b = json.dumps(
        {"p": payload_records, "ui": uninject_host_meta},
        ensure_ascii=False,
        separators=(",", ":"),
    ).encode("utf-8")
    misc_generic_b = json.dumps(
        {
            "cc": dict(complex_class_map),
            "ci": dict(complex_id_map),
            "mg": misc_generic_hide,
        },
        ensure_ascii=False,
        separators=(",", ":"),
    ).encode("utf-8")

    # Pack surrogate resources from local resource snapshots (deduplicated by name)
    res_by_name: dict[str, dict] = {}
    for res_path in RESOURCES_JSONS:
        if not os.path.exists(res_path):
            print(f"ERROR: Missing resources file: {res_path}", file=sys.stderr)
            return 1
        with open(res_path, encoding="utf-8") as f:
            for r in json.load(f):
                if r["name"] not in res_by_name:
                    res_by_name[r["name"]] = r
    res_raw = list(res_by_name.values())
    res_entries = []
    res_blob = bytearray()
    for r in res_raw:
        name = r["name"]
        aliases = r.get("aliases", []) or []
        deps = r.get("dependencies", []) or []
        kind_obj = r.get("kind", {})
        if isinstance(kind_obj, dict) and "mime" in kind_obj:
            mime = kind_obj["mime"]
            is_template = False
        else:
            mime = "template"
            is_template = True
        perm = 1 if name.startswith("trusted-") else 0
        raw_bytes = base64.b64decode(r["content"])
        off = len(res_blob)
        res_blob.extend(raw_bytes)
        res_entries.append({
            "n": name,
            "a": aliases,
            "m": mime,
            "tpl": is_template,
            "d": deps,
            "p": perm,
            "o": off,
            "l": len(raw_bytes),
        })
    res_index_b = json.dumps(res_entries, ensure_ascii=False, separators=(",", ":")).encode("utf-8")
    res_blob_b = bytes(res_blob)

    meta = {
        "format": "LEXSHLD2",
        "sources": [os.path.relpath(p, ROOT) for p in all_input_lists + RESOURCES_JSONS],
        "counts": {
            "rawLines": len(raw_lines),
            "pureHosts": len(sorted_pure),
            "patternRules": len(pattern_rules),
            "cspRules": len(csp_rules),
            "regexRules": len(regex_rules),
            "fallbackRules": len(fallback_rules),
            "tokenBuckets": len(tok_buckets),
            "optDomainBuckets": len(opt_dom_buckets),
            "simpleClasses": len(sc_hashes),
            "simpleIds": len(si_hashes),
            "cosmeticHosts": len(all_cos_hosts),
            "cosmeticUniquePayloads": len(payloads),
            "resources": len(res_entries),
        },
        "stats": dict(stats),
    }
    meta_b = json.dumps(meta, ensure_ascii=False, separators=(",", ":")).encode("utf-8")

    sections = [
        meta_b,              # 0
        pure_hashes_bytes,   # 1
        pure_masks_bytes,    # 2
        bytes(pat_recs),     # 3
        bytes(str_blob),     # 4
        hash_pool_b,         # 5
        tok_keys_b,          # 6
        tok_ranges_b,        # 7
        tok_vals_b,          # 8
        opt_keys_b,          # 9
        opt_ranges_b,        # 10
        opt_vals_b,          # 11
        fallback_b,          # 12
        sc_b,                # 13
        si_b,                # 14
        cos_hosts_hash_b,    # 15
        bytes(cos_host_slices_b),  # 16
        cos_pid_list_b,      # 17
        cos_payloads_b,      # 18
        misc_generic_b,      # 19
        res_index_b,         # 20
        res_blob_b,          # 21
        bytes(csp_recs),     # 22
        bytes(regex_recs),   # 23
    ]

    out = bytearray(b"LEXSHLD2")
    out.extend(struct.pack("<II", 2, len(sections)))
    for sec in sections:
        comp = zlib.compress(sec, 6)
        out.extend(struct.pack("<II", len(comp), len(sec)))
        out.extend(comp)

    os.makedirs(os.path.dirname(OUTPUT_PATH), exist_ok=True)
    with open(OUTPUT_PATH, "wb") as f:
        f.write(out)

    print(f"Wrote {OUTPUT_PATH}: {len(out) / 1048576.0:.2f} MB ({len(out):,} bytes)")
    print("Counts:", json.dumps(meta["counts"], indent=2))
    print("Stats:", json.dumps(meta["stats"], indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())

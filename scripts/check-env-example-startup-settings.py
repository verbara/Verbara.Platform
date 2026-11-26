#!/usr/bin/env python3
"""Start-up settings guard: the env file a stack hands its services lists every setting they refuse to start without.

Usage:
  check-env-example-startup-settings.py [--root DIR]

A stack such as docker/docker-compose.production.yml gives its services their settings through `env_file:`
(.env.production, copied by the operator from docker/.env.production.example). A setting the example leaves out
reaches no container, and a service that refuses to start without it restarts in a loop with the reason only in its
log. The settings are not listed here by hand. The script reads them from the code:
  - the Api's production check, the `if (app.Environment.IsProduction())` block of src/Verbara.Platform.Api/Program.cs:
    every configuration key that block reads, directly (`builder.Configuration["Key"]`) or through a variable set from
    one, is required; a key read only as the condition of `if (!string.IsNullOrEmpty(x)) { ... }` is not, and makes
    the keys inside that block required only while it has a value (the service's own settings over the Api's
    appsettings.json, which sets Asterisk:Ami:Hostname);
  - in each service's source folder, an options value handed to a constructor that refuses an empty argument
    (`new HmacCsatReplyTokenSigner(options.TokenSigningSecret, ...)`, with `options` the
    `IOptions<T>` bound by `Configure<T>(configuration.GetSection("Imap"))`), which the mail service builds at start.
    The constructors live outside this tree (Verbara.Sdk.Pro), so they are the one list kept here: REFUSES_EMPTY.
A required key `A:B` is the environment variable `A__B`.

Every compose file under docker/ and at the repository root is scanned, so a new stack is checked without being listed.
A service is recognised by what it runs: the Api built from the root Dockerfile or an image named `.../api` or
`...-api`; the mail, realtime and renderer services by their Dockerfile.<name> or an image named after them. The Api's
production check counts only when the service's ASPNETCORE_ENVIRONMENT is Production or unset (ASP.NET Core's
default). Only services with an `env_file:` are checked, and a required key passes when it is in one of those files
(the operator's file, or its `.example` copy when the operator's own is not in the tree) or in the service's
`environment:`, with any value, an empty placeholder included. A stack that sets everything inline is left to its own
defaults. A service with neither `build:` nor `image:` is an overlay and is left to the file it is layered on.
Each failure line names the file, the service, the setting, where the code requires it and what to add.

Limits: a key is found only through the `builder.Configuration["Key"]` indexer; a refusal that reads its key with
GetValue, GetConnectionString or GetSection is not seen. Refusals outside the production block (such as the Api's
ConnectionStrings:Postgres) are left to scripts/check-compose-postgres-password.py.

Exit codes:
  0  pass
  1  a service's env file lacks a setting the service refuses to start without
  2  usage error, a file that cannot be read, the production block or a required key not found, or no service checked
"""
import argparse
import glob
import importlib.util
import json
import os
import re
import sys

_HERE = os.path.dirname(os.path.abspath(__file__))
_ROOT = os.path.normpath(os.path.join(_HERE, os.pardir))

# Compose loading and interpolation are shared with the other compose guards, so all of them read a stack the same way.
_spec = importlib.util.spec_from_file_location("check_asterisk_realtime", os.path.join(_HERE, "check-asterisk-realtime.py"))
_compose = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(_compose)
interpolate = _compose.interpolate
Refused = _compose.Refused
InputError = _compose.InputError
load = _compose.load
_env = _compose._env

_EXIT_FAIL = 1
_EXIT_INPUT = 2

API_PROGRAM = os.path.join("src", "Verbara.Platform.Api", "Program.cs")
API_APPSETTINGS = os.path.join("src", "Verbara.Platform.Api", "appsettings.json")

# Each service the stacks run: its source folder, the Dockerfile it is built from (relative to the repository root)
# and the pattern of its published image name.
SERVICES = {
    "api": (os.path.join("src", "Verbara.Platform.Api"), "Dockerfile", re.compile(r"(?:^|[/-])api(?:[:@]|$)")),
    "mail": (os.path.join("src", "Verbara.Platform.Mail"), os.path.join("src", "Verbara.Platform.Mail", "Dockerfile.mail"),
             re.compile(r"(?:^|[/-])mail(?:[:@]|$)")),
    "realtime": (os.path.join("src", "Verbara.Platform.Realtime"),
                 os.path.join("src", "Verbara.Platform.Realtime", "Dockerfile.realtime"),
                 re.compile(r"(?:^|[/-])realtime(?:[:@]|$)")),
    "renderer": (os.path.join("src", "Verbara.Platform.Renderer"),
                 os.path.join("src", "Verbara.Platform.Renderer", "Dockerfile.renderer"),
                 re.compile(r"(?:^|[/-])renderer(?:[:@]|$)")),
}

# Constructors outside this tree that throw on an empty first argument (Verbara.Sdk.Pro.CsatRunner:
# HmacCsatReplyTokenSigner(string signingSecret, TimeSpan) calls ArgumentException.ThrowIfNullOrEmpty).
REFUSES_EMPTY = ("HmacCsatReplyTokenSigner",)

_ENV_LINE = re.compile(r"^\s*(?:export\s+)?([A-Za-z_][A-Za-z0-9_]*)\s*=(.*)$")
_PRODUCTION_IF = re.compile(r"if\s*\(\s*app\.Environment\.IsProduction\(\)\s*\)\s*\{")
_CONFIG_VAR = re.compile(r"\bvar\s+(\w+)\s*=\s*builder\.Configuration\[\"([^\"]+)\"\]")
_CONFIG_READ = re.compile(r"builder\.Configuration\[\"([^\"]+)\"\]")
_TRIGGER_IF = re.compile(r"if\s*\(\s*!\s*string\.IsNullOr(?:Empty|WhiteSpace)\(\s*(\w+)\s*\)\s*\)\s*\{")
_IDENT = re.compile(r"\b[A-Za-z_]\w*\b")
_LINE_COMMENT = re.compile(r"^[ \t]*//.*$", re.MULTILINE)
_STRING = re.compile(r'"(?:[^"\\\n]|\\.)*"')


class Requirement:
    """One setting a service refuses to start without; `when` is a key that must have a value for it to apply."""

    def __init__(self, key, where, when=None, production=False):
        self.key = key
        self.where = where
        self.when = when
        self.production = production

    @property
    def env_name(self):
        return self.key.replace(":", "__")


def _read(path):
    try:
        with open(path, encoding="utf-8") as fh:
            return fh.read()
    except OSError as e:
        raise InputError(f"cannot read {path}: {e}") from e


def _block(text, brace):
    """Returns (start, end) of the body of the `{` at index `brace`."""
    depth = 0
    for i in range(brace, len(text)):
        if text[i] == "{":
            depth += 1
        elif text[i] == "}":
            depth -= 1
            if depth == 0:
                return brace + 1, i
    raise InputError(f"unbalanced braces after offset {brace}")


def _line_of(text, index):
    return text.count("\n", 0, index) + 1


def api_production_requirements(root):
    """The keys the Api's production check refuses to start without, read from Program.cs."""
    path = os.path.join(root, API_PROGRAM)
    raw = _read(path)
    text = _LINE_COMMENT.sub(lambda m: " " * len(m.group(0)), raw)  # keeps offsets, so line numbers stay true
    m = _PRODUCTION_IF.search(text)
    if m is None:
        raise InputError(f"{API_PROGRAM}: no `if (app.Environment.IsProduction())` block found")
    start, end = _block(text, m.end() - 1)
    body = text[start:end]
    # A variable set from one configuration key: inside the block first, then anywhere in the file before it.
    var_keys = {}
    for vm in _CONFIG_VAR.finditer(text[:end]):
        var_keys.setdefault(vm.group(1), (vm.group(2), vm.start()))
    for vm in _CONFIG_VAR.finditer(body):
        var_keys[vm.group(1)] = (vm.group(2), start + vm.start())
    # `if (!string.IsNullOrEmpty(x)) { ... }`: x is a condition, the block's keys apply only while x has a value.
    triggers = []
    for tm in _TRIGGER_IF.finditer(body):
        var = tm.group(1)
        if var in var_keys:
            t_start, t_end = _block(body, tm.end() - 1)
            triggers.append((var_keys[var][0], start + t_start, start + t_end))
    trigger_keys = {t[0] for t in triggers}

    def condition(offset):
        return next((key for key, s, e in triggers if s <= offset < e), None)

    found = {}
    code = _STRING.sub(lambda s_: " " * len(s_.group(0)), body)  # a word inside a message is not a variable
    for im in _IDENT.finditer(code):
        var = im.group(0)
        if var not in var_keys:
            continue
        key, defined_at = var_keys[var]
        if key in trigger_keys:
            continue
        offset = start + im.start()
        found.setdefault(key, (condition(offset), _line_of(raw, defined_at)))
    for cm in _CONFIG_READ.finditer(body):
        key = cm.group(1)
        if key not in trigger_keys and key not in found:
            offset = start + cm.start()
            found[key] = (condition(offset), _line_of(raw, offset))
    if not found:
        raise InputError(f"{API_PROGRAM}: the production block reads no configuration key")
    return [Requirement(key, f"the production check, {API_PROGRAM.replace(os.sep, '/')}:{line}", when, production=True)
            for key, (when, line) in sorted(found.items())]


def refused_empty_requirements(root, folder):
    """Options values a service hands a constructor that refuses an empty argument, as `Section:Property` keys."""
    out = []
    files = sorted(glob.glob(os.path.join(root, folder, "**", "*.cs"), recursive=True))
    files = [f for f in files if os.sep + "obj" + os.sep not in f and os.sep + "bin" + os.sep not in f]
    texts = {f: _read(f) for f in files}
    sections = {}
    for text in texts.values():
        for sm in re.finditer(r"Configure<(\w+)>\(\s*\w+\.GetSection\(\"([^\"]+)\"\)\s*\)", text):
            sections[sm.group(1)] = sm.group(2)
    for path, text in texts.items():
        for name in REFUSES_EMPTY:
            for cm in re.finditer(r"new\s+" + name + r"\(\s*(\w+)\.(\w+)\s*[,)]", text):
                var, prop = cm.group(1), cm.group(2)
                bound = None
                for om in re.finditer(r"\b" + var + r"\s*=\s*\w+\.GetRequiredService<IOptions<(\w+)>>\(\)\.Value",
                                      text[:cm.start()]):
                    bound = om.group(1)
                rel = os.path.relpath(path, root).replace(os.sep, "/")
                line = _line_of(text, cm.start())
                if bound is None or bound not in sections:
                    raise InputError(f"{rel}:{line}: {name} gets {var}.{prop}, and the section {var} is bound "
                                     f"from was not found")
                out.append(Requirement(f"{sections[bound]}:{prop}", f"{name} refuses an empty value, {rel}:{line}"))
    return out


def _appsettings_value(root, key):
    try:
        model = json.loads(_read(os.path.join(root, API_APPSETTINGS)))
    except ValueError as e:
        raise InputError(f"{API_APPSETTINGS}: {e}") from e
    node = model
    for part in key.split(":"):
        if not isinstance(node, dict):
            return None
        node = next((v for k, v in node.items() if k.lower() == part.lower()), None)
    return None if node is None or isinstance(node, (dict, list)) else str(node)


def _kind(service, compose_dir, root):
    build = service.get("build")
    if build is not None and not isinstance(build, (str, dict)):
        build = None  # `build: !reset []` in an overlay: the image below decides
    if build is not None:
        context = build if isinstance(build, str) else str(build.get("context") or ".")
        dockerfile = "Dockerfile" if isinstance(build, str) else str(build.get("dockerfile") or "Dockerfile")
        built = os.path.normpath(os.path.join(compose_dir, context, dockerfile))
        for kind, (_, dockerfile_rel, _) in SERVICES.items():
            if built == os.path.normpath(os.path.join(root, dockerfile_rel)):
                return kind
        return None
    image = str(service.get("image") or "")
    for kind, (_, _, pattern) in SERVICES.items():
        if pattern.search(image):
            return kind
    return None


def _env_file_paths(service):
    entries = service.get("env_file") or []
    if not isinstance(entries, list):
        entries = [entries]
    for entry in entries:
        yield str(entry.get("path", "")) if isinstance(entry, dict) else str(entry)


def _read_env_file(path):
    keys = {}
    for line in _read(path).splitlines():
        m = _ENV_LINE.match(line)
        if m:
            keys[m.group(1).lower()] = m.group(2).strip()
    return keys


def check_service(rel, name, service, compose_dir, root, requirements):
    """Returns (checked, failure lines) for one service."""
    kind = _kind(service, compose_dir, root)
    files = list(_env_file_paths(service))
    if kind is None or not files:
        return False, []
    seen = {}
    shown = []
    missing = []
    for entry in files:
        path = os.path.normpath(os.path.join(compose_dir, entry))
        found = next((p for p in (path, path + ".example") if os.path.isfile(p)), None)
        if found is None:
            missing.append(os.path.relpath(path, root).replace(os.sep, "/"))
            continue
        shown.append(os.path.relpath(found, root).replace(os.sep, "/"))
        seen.update(_read_env_file(found))
    for k, v in _env(service).items():
        seen[str(k).lower()] = None if v is None else str(v)
    if missing and not shown:
        return True, [f"{rel}: {name} reads its settings from {', '.join(missing)}, which is not in the tree, nor its "
                      f".example, so nothing shows the operator what {name} needs to start"]
    try:
        environment = interpolate(seen.get("aspnetcore_environment") or "", {})
    except Refused:
        environment = ""
    failures = []
    for req in requirements.get(kind, []):
        if req.production and environment not in ("", "Production"):
            continue
        if req.when is not None:
            value = seen.get(req.when.replace(":", "__").lower())
            if value is None:
                value = _appsettings_value(root, req.when) if kind == "api" else None
            if not value:
                continue
        if req.env_name.lower() in seen:
            continue
        cond = f" while {req.when.replace(':', '__')} is set" if req.when else ""
        failures.append(f"{rel}: {name} refuses to start without {req.env_name}{cond} ({req.where}), and "
                        f"{', '.join(shown)} has no {req.env_name} line; add {req.env_name}= to it with a comment "
                        f"on what it is")
    return True, failures


def main(argv=None):
    ap = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    ap.add_argument("--root", default=_ROOT, help="repository root (tests)")
    args = ap.parse_args(argv)
    root = os.path.abspath(args.root)
    failures = []
    checked = []
    try:
        requirements = {"api": api_production_requirements(root)}
        for kind, (folder, _, _) in SERVICES.items():
            requirements.setdefault(kind, [])
            requirements[kind] += refused_empty_requirements(root, folder)
        files = sorted(set(
            glob.glob(os.path.join(root, "docker", "**", "*.yml"), recursive=True)
            + glob.glob(os.path.join(root, "docker", "**", "*.yaml"), recursive=True)
            + glob.glob(os.path.join(root, "docker-compose*.yml"))
            + glob.glob(os.path.join(root, "docker-compose*.yaml"))
            + glob.glob(os.path.join(root, "compose*.yml"))
            + glob.glob(os.path.join(root, "compose*.yaml"))))
        for path in files:
            rel = os.path.relpath(path, root).replace(os.sep, "/")
            compose_dir = os.path.dirname(path)
            for name, service in load(path).items():
                if not isinstance(service, dict):
                    continue
                was_checked, found = check_service(rel, name, service, compose_dir, root, requirements)
                if was_checked:
                    checked.append(f"{rel}:{name}")
                failures += found
    except InputError as e:
        print(f"check-env-example-startup-settings: {e}", file=sys.stderr)
        return _EXIT_INPUT
    if not checked:
        print("check-env-example-startup-settings: no compose service under docker/ reads its settings from an "
              "env_file", file=sys.stderr)
        return _EXIT_INPUT
    for line in failures:
        print(line)
    if failures:
        return _EXIT_FAIL
    keys = sorted({r.env_name + (f" (while {r.when.replace(':', '__')} is set)" if r.when else "")
                   for reqs in requirements.values() for r in reqs})
    print(f"check-env-example-startup-settings: {len(checked)} service(s) list every start-up setting ("
          + ", ".join(keys) + "): " + ", ".join(checked))
    return 0


if __name__ == "__main__":
    sys.exit(main())

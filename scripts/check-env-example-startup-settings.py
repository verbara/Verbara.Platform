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
  - in each service's source folder, a file that names a switch (`const string SwitchKey = "Imap:Enabled"`) and the
    settings the service refuses to start without while that switch is true (`Require(failures, "Key", value)`, a
    `{i}` index read as 0; `RequireEither`, a key that falls back to one required elsewhere, is not one of them).
A required key `A:B` is the environment variable `A__B`.

Every compose file under docker/ and at the repository root is scanned, so a new stack is checked without being listed.
A service is recognised by what it runs: the Api built from the root Dockerfile or an image named `.../api` or
`...-api`; the mail, realtime and renderer services by their Dockerfile.<name> or an image named after them. The Api's
production check counts only when the service's ASPNETCORE_ENVIRONMENT is Production or unset (ASP.NET Core's
default). A service with an `env_file:` passes a required key when it is in one of those files
(the operator's file, or its `.example` copy when the operator's own is not in the tree) or in the service's
`environment:`, with any value, an empty placeholder included. A setting needed while a switch is true is checked even
when the file leaves the switch off: the file must carry a line for the switch and one for each such setting, set or
commented out (`# Key=`), so the operator who turns the switch on finds them next to it; when the file sets the switch
to true, each setting must have a value (an empty `Key=`, or one that interpolates to empty, fails, as the service
refuses it).

A service with no `env_file:` passes its settings inline (`environment:` with `${VAR:-default}`), and the operator
fills the variables from the stack's example: docker-compose.<name>.yml reads .env.<name>.example next to it
(docker-compose.yml and compose.yml read .env.example). Each required key the service sets inline is interpolated with
that example as given. It fails when the value is a default the check refuses besides an empty one (`key ==
"value"` in the production block, such as the built-in service key: `${VAR:-default}` falls back to it also when the
example leaves VAR empty, so the stack must refuse to start without VAR, `${VAR:?...}`, instead); when compose refuses
to start without a variable (`${VAR:?...}`) the example has no line for; or when the value is empty and the example
has no line for its variable. A variable the example lists, set, empty or commented out, is the operator's to fill,
as an empty `Key=` in an env_file is. A required key the service does not set inline, and the switch settings, are
left to the service's own defaults. A service with neither `build:` nor `image:` is an overlay and is left to the file
it is layered on.
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

_ENV_LINE = re.compile(r"^\s*(?:export\s+)?([A-Za-z_][A-Za-z0-9_]*)\s*=(.*)$")
_PRODUCTION_IF = re.compile(r"if\s*\(\s*app\.Environment\.IsProduction\(\)\s*\)\s*\{")
_CONFIG_VAR = re.compile(r"\bvar\s+(\w+)\s*=\s*builder\.Configuration\[\"([^\"]+)\"\]")
_CONFIG_READ = re.compile(r"builder\.Configuration\[\"([^\"]+)\"\]")
_TRIGGER_IF = re.compile(r"if\s*\(\s*!\s*string\.IsNullOr(?:Empty|WhiteSpace)\(\s*(\w+)\s*\)\s*\)\s*\{")
_IDENT = re.compile(r"\b[A-Za-z_]\w*\b")
_LINE_COMMENT = re.compile(r"^[ \t]*//.*$", re.MULTILINE)
_STRING = re.compile(r'"(?:[^"\\\n]|\\.)*"')
_COMMENTED_ENV_LINE = re.compile(r"^\s*#\s*(?:export\s+)?([A-Za-z_][A-Za-z0-9_]*)\s*=")
_SWITCH_KEY = re.compile(r"\bconst\s+string\s+SwitchKey\s*=\s*\"([^\"]+)\"")
_REQUIRE = re.compile(r"\bRequire\(\s*\w+\s*,\s*\$?\"([^\"]+)\"")
_INDEX = re.compile(r"\{[^}]*\}")
_REFUSED_LITERAL = re.compile(r"\b(\w+)\s*==\s*\"([^\"]*)\"")
_VARIABLE = re.compile(r"\$(?:\{([A-Za-z_][A-Za-z0-9_]*)|([A-Za-z_][A-Za-z0-9_]*))")


class Requirement:
    """One setting a service refuses to start without; `when` is a key that must have a value for it to apply, and
    `switch` a key whose true value it applies under (the env file must still list it while the switch is off)."""

    def __init__(self, key, where, when=None, production=False, switch=None, refused=()):
        self.key = key
        self.where = where
        self.when = when
        self.production = production
        self.switch = switch
        self.refused = frozenset(refused)  # values the check refuses besides an empty one (a built-in default)

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
    # `x == "value"`: the check refuses that value of x's key as it refuses an empty one (the built-in default).
    refused = {}
    for lm in _REFUSED_LITERAL.finditer(body):
        if lm.group(1) in var_keys:
            refused.setdefault(var_keys[lm.group(1)][0], set()).add(lm.group(2))
    return [Requirement(key, f"the production check, {API_PROGRAM.replace(os.sep, '/')}:{line}", when, production=True,
                        refused=refused.get(key, ()))
            for key, (when, line) in sorted(found.items())]


def switch_requirements(root, folder):
    """Settings a service refuses to start without while a switch is true, as `Section:Property` keys."""
    out = []
    files = sorted(glob.glob(os.path.join(root, folder, "**", "*.cs"), recursive=True))
    files = [f for f in files if os.sep + "obj" + os.sep not in f and os.sep + "bin" + os.sep not in f]
    for path in files:
        raw = _read(path)
        text = _LINE_COMMENT.sub(lambda m: " " * len(m.group(0)), raw)
        requires = list(_REQUIRE.finditer(text))
        if not requires:
            continue
        rel = os.path.relpath(path, root).replace(os.sep, "/")
        switch = _SWITCH_KEY.search(text)
        if switch is None:
            raise InputError(f"{rel}:{_line_of(text, requires[0].start())}: Require(...) names a setting, and the file "
                             f"has no `const string SwitchKey` saying which switch it is required under")
        for rm in requires:
            key = _INDEX.sub("0", rm.group(1))
            out.append(Requirement(key, f"{rel}:{_line_of(text, rm.start())}", switch=switch.group(1)))
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


def _read_commented_keys(path):
    keys = set()
    for line in _read(path).splitlines():
        m = _COMMENTED_ENV_LINE.match(line)
        if m:
            keys.add(m.group(1).lower())
    return keys


def stack_example(path):
    """The `.env` example of a stack that passes its settings inline: docker-compose.<name>.yml reads
    .env.<name>.example next to it (docker-compose.yml and compose.yml read .env.example), or None."""
    base = os.path.basename(path)
    m = re.match(r"^(?:docker-)?compose(?:\.(.+))?\.ya?ml$", base)
    if m is None:
        return None
    example = os.path.join(os.path.dirname(path), f".env.{m.group(1)}.example" if m.group(1) else ".env.example")
    return example if os.path.isfile(example) else None


def _read_variables(path):
    """(values, listed) of an env file as compose interpolates it: names keep their case; `listed` also holds the
    names on a commented line (`# NAME=`)."""
    values, listed = {}, set()
    for line in _read(path).splitlines():
        m = _ENV_LINE.match(line)
        if m:
            value = m.group(2).strip()
            if len(value) >= 2 and value[0] == value[-1] and value[0] in "\"'":
                value = value[1:-1]
            values[m.group(1)] = value
            listed.add(m.group(1))
            continue
        m = _COMMENTED_ENV_LINE.match(line)
        if m:
            listed.add(m.group(1))
    return values, listed


def check_inline(rel, name, kind, service, root, requirements, example):
    """A service that passes its settings inline (`environment:` with `${VAR:-default}`), interpolated with the stack's
    example as an operator copies it. Returns (checked, failure lines); a setting the service does not set inline is
    left to its own defaults."""
    env = {str(k).lower(): (str(k), None if v is None else str(v)) for k, v in _env(service).items()}
    values, listed = _read_variables(example) if example else ({}, set())
    shown = os.path.relpath(example, root).replace(os.sep, "/") if example else None

    def value_of(raw):
        try:
            return interpolate(raw, values)
        except Refused:
            return None  # compose refuses to start without it, so it has a value whenever the service runs

    environment = value_of(env.get("aspnetcore_environment", (None, ""))[1] or "") or ""
    checked = False
    failures = []
    for req in requirements.get(kind, []):
        if req.switch is not None or req.env_name.lower() not in env:
            continue
        if req.production and environment not in ("", "Production"):
            continue
        if req.when is not None:
            when = env.get(req.when.replace(":", "__").lower())
            if when is None:
                when_set = bool(_appsettings_value(root, req.when)) if kind == "api" else False
            else:
                when_value = value_of(when[1]) if when[1] is not None else None
                when_set = when_value is None or bool(when_value)
            if not when_set:
                continue
        checked = True
        raw = env[req.env_name.lower()][1]
        if raw is None:
            continue  # `KEY` with no value: passed through from the shell that runs compose
        names = [a or b for a, b in _VARIABLE.findall(raw)]
        unlisted = [n for n in names if n not in listed]
        where = shown or f"no .env example next to {rel}"
        try:
            value = interpolate(raw, values)
        except Refused as e:
            if str(e) not in listed:
                failures.append(f"{rel}: {name} sets {req.env_name} from {e} ({req.where}), compose refuses to start "
                                f"without {e}, and {where} has no {e} line; add {e}= to "
                                f"{shown or 'the stack' + chr(39) + 's .env example'} with a comment on what it is")
            continue
        if value in req.refused:
            fix = (f"make compose refuse to start without {names[0]} (${{{names[0]}:?...}}) and add {names[0]}= to "
                   f"{shown or 'the stack' + chr(39) + 's .env example'}, with a comment on what it is") if names \
                else f"set {req.env_name} from a variable the stack's .env example names"
            failures.append(f"{rel}: {name} refuses to start with {req.env_name} at the built-in default "
                            f"({req.where}), and {rel} sets it to that default from {where}; {fix}")
        elif not value.strip().strip("\"'") and (not names or unlisted):
            missing = ", ".join(unlisted) or req.env_name
            failures.append(f"{rel}: {name} refuses to start without {req.env_name} ({req.where}), {rel} sets it "
                            f"empty, and {where} has no {missing} line; add {missing}= to it with a comment on what "
                            f"it is")
    return checked, failures


def check_service(rel, name, service, compose_dir, root, requirements, example=None):
    """Returns (checked, failure lines) for one service."""
    kind = _kind(service, compose_dir, root)
    files = list(_env_file_paths(service))
    if kind is None:
        return False, []
    if not files:
        return check_inline(rel, name, kind, service, root, requirements, example)
    seen = {}
    commented = set()
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
        commented |= _read_commented_keys(found)
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
    switches_reported = set()
    for req in requirements.get(kind, []):
        if req.switch is not None:
            failures += _check_switched(rel, name, req, seen, commented, shown, switches_reported)
            continue
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


def _has_value(seen, key):
    """A key with a non-empty value once interpolated. `KEY` with no `=` in `environment:` (None, passed through from
    the shell) and a `${X:?...}` that compose itself refuses to leave empty both count as set."""
    if key not in seen:
        return False
    raw = seen[key]
    if raw is None:
        return True
    try:
        value = interpolate(raw, {})
    except Refused:
        return True
    return value.strip().strip("\"'") != ""


def _check_switched(rel, name, req, seen, commented, shown, switches_reported):
    """A setting needed while a switch is true: listed (set or commented) next to the switch, set when it is on."""
    switch_env = req.switch.replace(":", "__")
    files = ", ".join(shown) or "the service's environment"
    out = []
    raw = seen.get(switch_env.lower())
    try:
        value = interpolate(raw, {}) if raw is not None else None
    except Refused:
        value = raw
    on = value is not None and value.strip().strip("\"'").lower() == "true"
    if raw is None and switch_env.lower() not in commented and switch_env not in switches_reported:
        switches_reported.add(switch_env)
        out.append(f"{rel}: {name} reads {switch_env}, and {files} has no {switch_env} line; add # {switch_env}=true "
                   f"to it, with the settings it needs (the first: {req.env_name}, {req.where})")
    key = req.env_name.lower()
    if on and not _has_value(seen, key):
        out.append(f"{rel}: {name} needs {req.env_name} while {switch_env} is true ({req.where}), and {files} sets "
                   f"{switch_env}=true with no {req.env_name} value; give {req.env_name} a value in it")
    elif not on and key not in seen and key not in commented:
        out.append(f"{rel}: {name} needs {req.env_name} while {switch_env} is true ({req.where}), and {files} has no "
                   f"{req.env_name} line; add # {req.env_name}= next to {switch_env} with a comment on what it is")
    return out


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
            requirements[kind] += switch_requirements(root, folder)
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
            example = stack_example(path)
            for name, service in load(path).items():
                if not isinstance(service, dict):
                    continue
                was_checked, found = check_service(rel, name, service, compose_dir, root, requirements, example)
                if was_checked:
                    checked.append(f"{rel}:{name}")
                failures += found
    except InputError as e:
        print(f"check-env-example-startup-settings: {e}", file=sys.stderr)
        return _EXIT_INPUT
    if not checked:
        print("check-env-example-startup-settings: no compose service under docker/ reads its settings from an "
              "env_file or sets a start-up setting inline", file=sys.stderr)
        return _EXIT_INPUT
    for line in failures:
        print(line)
    if failures:
        return _EXIT_FAIL
    def label(r):
        if r.switch:
            return f" (while {r.switch.replace(':', '__')} is true)"
        return f" (while {r.when.replace(':', '__')} is set)" if r.when else ""
    keys = sorted({r.env_name + label(r) for reqs in requirements.values() for r in reqs})
    print(f"check-env-example-startup-settings: {len(checked)} service(s) list every start-up setting ("
          + ", ".join(keys) + "): " + ", ".join(checked))
    return 0


if __name__ == "__main__":
    sys.exit(main())

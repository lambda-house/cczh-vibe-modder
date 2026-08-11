#!/usr/bin/env bash
# e2e.sh — build, run the full verification gate, then the balance demos.
# Base-unit state hashes are pinned below: content may grow (new units, new
# factions) without ever perturbing an existing replay, and this catches it if
# layering ever renumbers a prototype.
# Every gate verb exits non-zero on failure, so set -e makes this a real test.
set -euo pipefail

cd "$(dirname "$0")"

export DOTNET_ROOT="${DOTNET_ROOT:-$HOME/.dotnet}"
export PATH="$DOTNET_ROOT:$PATH"
export DOTNET_CLI_TELEMETRY_OPTOUT=1

echo "== build =="
dotnet build -c Release --nologo -v quiet
echo "build: OK"

rts() { dotnet bin/Release/net8.0/rts.dll "$@"; }

# Gate numbering is DERIVED, never written down. It used to be literal ("gate 7/22"), which
# made the denominator appear on all 22 lines: two branches each appending one gate both
# renumber the whole file and conflict on every one of them. Counting the call sites in this
# script makes adding a gate a pure append, which is what lets slices land in parallel.
GATE_TOTAL=$(grep -c '^gate ' "$0")
GATE_N=0
gate() { GATE_N=$((GATE_N + 1)); echo; echo "== gate $GATE_N/$GATE_TOTAL: $* =="; }

gate "content lint"
rts lint

gate "replay determinism"
rts replay --a crusader --b battlemaster --seed 7

gate "duel smoke"
rts duel --a crusader --b technical --n 20 --seed 42

gate "econ determinism"
rts econ --a "technical*" --b "war_factory,crusader*" --n 20 --seed 42

gate "faction layering resolves"
rts faction

echo
echo "== demo: set-piece counter matrix (base units + general variants) =="
rts matrix --n 100 --seed 1

echo
echo "== demo: macro reversal (battlemaster wins the trickle war it loses set-piece) =="
rts econ --a "war_factory,battlemaster*" --b "war_factory,crusader*" --n 50 --seed 42

echo
echo "== demo: greedy opening punished by rush =="
rts econ --a "barracks,ranger*4,war_factory,crusader*" --b "technical*" --n 50 --seed 42

gate "layered packs + diff"
# A mod is a patch over a base pack. Three properties are asserted:
#   1. a stack lints and resolves (the mod does not need to restate the base)
#   2. `rts diff` names exactly what changed, by category
#   3. the base pack is UNAFFECTED — loading a mod must not mutate base results
rts lint --mod content/mods/glass-cannon.json | tail -1
changes=$(rts diff --head-mod content/mods/glass-cannon.json --json \
          | python3 -c "import sys,json; print(json.load(sys.stdin)['changes'])")
[ "$changes" = "4" ] || { echo "  DIFF DRIFT: expected 4 changes, got $changes"; exit 1; }
echo "  diff: 4 changes detected (2 unit stats, 1 weapon, 1 matrix cell)"

base_only=$(rts duel --a crusader --b battlemaster --n 20 --seed 42 --json \
            | sed -n 's/.*"lastFinalHash": "\(.*\)".*/\1/p')
with_mod=$(rts duel --a crusader --b battlemaster --n 20 --seed 42 --json \
           --mod content/mods/glass-cannon.json | sed -n 's/.*"lastFinalHash": "\(.*\)".*/\1/p')
[ "$base_only" != "$with_mod" ] || { echo "  MOD HAD NO EFFECT: $base_only"; exit 1; }
echo "  mod changes the sim: $base_only -> $with_mod"

base_again=$(rts duel --a crusader --b battlemaster --n 20 --seed 42 --json \
             | sed -n 's/.*"lastFinalHash": "\(.*\)".*/\1/p')
[ "$base_again" = "$base_only" ] || { echo "  BASE CONTAMINATED: $base_again != $base_only"; exit 1; }
echo "  base unaffected by the mod: $base_again"

gate "the total resolution order"
# Three mechanisms compose content and they run in ONE order, each exactly once:
#   1. LAYER    packs fold in ordinal order; per-key last-wins; null removes
#   2. DEFAULT  the composed `defaults` block fills what a unit left unstated
#   3. INHERIT  faction `extends`, then per-faction remove -> add -> modify
# There is deliberately no entity-level `extends`. Every claim below is one of those
# adjacencies, and each is here because getting it wrong is silent.
ORD=$(mktemp -d)

# --- 1. The merge must be TOTAL over the DTO, and the guard must actually bite. -------
# `defaults` was dropped by the merge for the whole life of the layering feature: absent
# from the object initializer, so every multi-layer stack quietly reverted to the
# compiler's built-ins. Nothing failed. It is the same shape as CloneForFaction copying 9
# of 20 fields. So the guard is tested GENERATIVELY — add a property, prove the loader
# refuses to run, put it back — rather than asserted by reading the code.
#
# EVERY hand-written merge is probed, not just the root one. ZhTargetDto and UnitDefaultsDto
# are merged by object initializers of exactly the same shape, and ZhTargetDto is the one two
# parallel branches are most likely to both extend — git merges two added initializer lines
# without complaint, and a dropped one reverts the field silently in every layered stack.
SCHEMA_BAK="$ORD/Schema.cs.bak"
cp Content/Schema.cs "$SCHEMA_BAK"
probe_one() {                    # <anchor> <probe-property> <expected-type-name>
  cp "$SCHEMA_BAK" Content/Schema.cs
  ANCHOR="$1" PROBE="$2" python3 - <<'PY'
import os
p = 'Content/Schema.cs'; s = open(p).read()
anchor, probe = os.environ['ANCHOR'], os.environ['PROBE']
assert anchor in s, f"anchor moved; the totality probe needs updating: {anchor}"
s = s.replace(anchor, anchor + f"\n    public List<string> {probe} {{ get; set; }} = new();", 1)
open(p, 'w').write(s)
PY
  probe_build=1
  dotnet build -c Release --nologo -v quiet >/dev/null 2>&1 || probe_build=0
  probe_out=""
  [ "$probe_build" = 1 ] && probe_out=$(rts lint 2>&1 || true)
  cp "$SCHEMA_BAK" Content/Schema.cs
  case "$probe_out" in
    *"not total over $3"*"$2"*)
      echo "  merge totality enforced over $3: an unmerged property fails the load" ;;
    *) echo "  TOTALITY GUARD DID NOT FIRE for $3: a new property would be silently dropped"
       echo "  got: ${probe_out:-<build failed>}"
       dotnet build -c Release --nologo -v quiet >/dev/null; rm -rf "$ORD"; exit 1 ;;
  esac
}
probe_one "    public ZhTargetDto Zh { get; set; } = new();" E2EUnmergedProbe ContentPackDto
probe_one "public sealed class ZhTargetDto
{" E2EZhProbe ZhTargetDto
probe_one "public sealed class UnitDefaultsDto
{" E2EDefaultsProbe UnitDefaultsDto
dotnet build -c Release --nologo -v quiet >/dev/null

# --- 2/3. Absent is not the same as DEFAULT. -----------------------------------------
# `defaults` and `lint` are whole blocks with compiler-supplied fallbacks, so a layer that
# says nothing about them must INHERIT, not overwrite with the fallback. The witness is
# chosen to be unmissable in both directions: `scout` states no cost at all, so losing
# defaults turns it into a hard "cost must be > 0" error, and the band [9000, 9001] cannot
# be confused with the built-in [20, 200].
python3 - "$ORD" <<'PY'
import json, re, sys
d = json.loads(re.sub(r'//.*', '', open('content/game.json').read()))
d['meta'] = {'name': 'withdefaults', 'version': 1}
d['defaults'] = {'unit': {'cost': 1234}}
d['lint'] = {'dpsPer1000CostBand': [9000, 9001]}
scout = dict(d['units']['ranger'])
for k in ('cost', 'buildSeconds', 'buildTicks'): scout.pop(k, None)
d['units']['scout'] = scout
d['factions']['usa']['units'] = d['factions']['usa']['units'] + ['scout']
open(f'{sys.argv[1]}/base.json', 'w').write(json.dumps(d))
json.dump({'meta': {'name': 'silent', 'version': 1}}, open(f'{sys.argv[1]}/silent.json', 'w'))
PY
layered=$(rts lint --content "$ORD/base.json" --mod "$ORD/silent.json" 2>&1)
case "$layered" in
  *"cost must be > 0"*) echo "  DEFAULTS LOST: a silent layer reverted them to the built-ins"
                        rm -rf "$ORD"; exit 1 ;;
esac
case "$layered" in
  *"band [9000, 9001]"*) : ;;
  *) echo "  LINT CONFIG LOST: a silent layer reverted the band to the built-in"
     rm -rf "$ORD"; exit 1 ;;
esac
echo "  a layer that states no defaults/lint inherits them instead of overwriting"

# --- 4/5. null REMOVES, and a removal that matches nothing is an error. ---------------
# Removal is what makes "pack patch removes what a faction modify replaced" answerable at
# all. The answer is that it is not a merge conflict: removal lands in stage 1, so by stage
# 3 the name does not exist and every reference to it is an ordinary dangling reference —
# reported with the layer that took it away, because an error that names its cause is a
# repair instruction and one that does not is a scavenger hunt.
echo '{ "meta": { "name": "nocrusader", "version": 1 }, "units": { "crusader": null } }' > "$ORD/rm.json"
echo '{ "meta": { "name": "typo", "version": 1 }, "units": { "crusadar": null } }' > "$ORD/typo.json"
rm_out=$(rts lint --mod "$ORD/rm.json" 2>&1 || true)
case "$rm_out" in
  *"faction 'usa': unknown unit 'crusader' — removed by layer 2 ('nocrusader')"*)
    echo "  null removes a key, and the dangling reference names the layer that did it" ;;
  *) echo "  REMOVAL NOT REPORTED WITH PROVENANCE"; echo "$rm_out"; rm -rf "$ORD"; exit 1 ;;
esac
typo_out=$(rts lint --mod "$ORD/typo.json" 2>&1 || true)
case "$typo_out" in
  *"removes 'crusadar', which no earlier layer declared"*)
    echo "  removing a name nothing declared is an error, not a silent no-op" ;;
  *) echo "  A NO-OP REMOVAL WAS ACCEPTED"; echo "$typo_out"; rm -rf "$ORD"; exit 1 ;;
esac

# --- 6/7. Within a faction: remove -> add -> modify, and both adjacencies matter. -----
# remove-before-add is the DE-FORK idiom (drop the parent's variant, re-adopt the shared
# prototype); under add-first it would be a duplicate-key error and there would be no way
# to say "inherit everything except this one fork".
# remove-before-modify makes the contradictory pair a deterministic ERROR; under
# modify-first the patch is computed, a variant allocated, and then discarded — work whose
# only trace is a unit that is mysteriously absent.
cat > "$ORD/defork.json" <<'EOF'
{ "meta": { "name": "defork", "version": 1 },
  "factions": { "china_stock": { "extends": "china_tank",
                                 "remove": ["battlemaster"], "add": ["battlemaster"] } } }
EOF
cat > "$ORD/contradict.json" <<'EOF'
{ "meta": { "name": "contradict", "version": 1 },
  "factions": { "china_broken": { "extends": "china_tank", "remove": ["battlemaster"],
                                  "modify": { "battlemaster": { "cost": 100 } } } } }
EOF
defork=$(rts faction --mod "$ORD/defork.json" 2>&1)
parent_cost=$(printf '%s' "$defork" | sed -n 's/.*china_tank\/battlemaster *cost= *\([0-9]*\).*/\1/p')
child=$(printf '%s' "$defork" | awk '/china_stock/{f=1} f&&/-> battlemaster /{print; exit}')
case "$child" in
  *"-> battlemaster"*"+added"*) : ;;
  *) echo "  DE-FORK BROKEN: remove+add did not re-adopt the shared prototype"
     echo "$defork"; rm -rf "$ORD"; exit 1 ;;
esac
child_cost=$(printf '%s' "$child" | sed -n 's/.*cost= *\([0-9]*\).*/\1/p')
[ -n "$parent_cost" ] && [ "$child_cost" != "$parent_cost" ] \
  || { echo "  DE-FORK BROKEN: child cost $child_cost still equals the fork's $parent_cost"
       rm -rf "$ORD"; exit 1; }
echo "  remove->add de-forks: child re-adopts the shared prototype ($child_cost, not $parent_cost)"
contra=$(rts lint --mod "$ORD/contradict.json" 2>&1 || true)
case "$contra" in
  *"modify 'battlemaster' is not in the roster"*)
    echo "  remove->modify is a deterministic error, never a silently discarded patch" ;;
  *) echo "  CONTRADICTORY ROSTER OPS WERE ACCEPTED"; echo "$contra"; rm -rf "$ORD"; exit 1 ;;
esac

# --- 8. The zh target block composes through the SAME merge. -------------------------
# It used to be folded by a second, near-identical loop in ContentDb.LoadZhTarget: two
# implementations of one contract, which is how they drift.
# The witness is per-KEY union across layers, not last-layer-wins: the emitted objects must
# name a model the BASE pack maps (AVAmbulance for ranger) and one only the MOD maps
# (RTSMAST for its factory). Losing either half would still compile cleanly.
rts compile --target zh --out "$ORD/zh" --mod content/mods/demo-attach.json >/dev/null
zh_obj="$ORD/zh/Data/INI/Object/demo.ini"
for m in AVAmbulance RTSMAST; do
  grep -q "$m" "$zh_obj" \
    || { echo "  ZH BLOCK LOST IN THE MERGE: no '$m' in the emitted objects"; rm -rf "$ORD"; exit 1; }
done
grep -q "ARMOR_PIERCING" "$ORD/zh/Data/INI/Weapon/demo.ini" \
  || { echo "  ZH DAMAGE-TYPE MAP LOST: the base layer's mapping did not survive"; rm -rf "$ORD"; exit 1; }
echo "  zh target data composes per key through the one merge (base + mod models both emitted)"

# --- 9. A removal must be USABLE, not merely diagnosable — and diffed without phantoms.
# The weapon table is a dictionary sorted into an array, so removing any weapon renumbers
# every index after it. `rts diff` used to compare those indices and reported each shifted
# unit as changed, with IDENTICAL names on both sides of the arrow: 6 changes where there
# were 3, and a duplication ratio of 40% where it was 100%. Same rule as prototype
# identity — an ordinal is an array position and never stands in for a name.
cat > "$ORD/drop.json" <<'EOF'
{ "meta": { "name": "nocrusader", "version": 2 },
  "units": { "crusader": null },
  "weapons": { "crusader_cannon": null },
  "factions": { "usa": { "units": ["ranger"] },
                "usa_laser": { "extends": "usa", "add": ["laser_crusader"] } } }
EOF
rts lint --mod "$ORD/drop.json" | tail -1 | grep -q "^lint: OK$" \
  || { echo "  A CONSISTENT REMOVAL DID NOT LINT"; rts lint --mod "$ORD/drop.json" | tail -3
       rm -rf "$ORD"; exit 1; }
drop_changes=$(rts diff --head-mod "$ORD/drop.json" --json \
               | python3 -c "import sys,json; print(json.load(sys.stdin)['changes'])")
[ "$drop_changes" = "3" ] \
  || { echo "  PHANTOM DIFF: expected 3 changes (unit, weapon, roster), got $drop_changes"
       rts diff --head-mod "$ORD/drop.json"; rm -rf "$ORD"; exit 1; }
echo "  a consistent removal lints, and diffs as exactly 3 changes with no index phantoms"
rm -rf "$ORD"
gate "structures are economic targets"
# A structure is a unit with speed 0, KindOf role flags and BuildCompletion=PLACED_BY_PLAYER
# — ZH's model exactly. Three properties:
#   1. object prerequisites are REVOCABLE: no live factory => no units, however rich you are
#   2. a team that can never produce again is defeated, not left alive until the tick cap
#   3. all of it is opt-in by content, so packs without structures are bit-identical
rts lint --mod content/mods/structures.json | tail -1

# B declares no factory at all: instantly out, because money cannot buy what has no producer.
nofac=$(rts econ --a "usa_power_plant,usa_factory,crusader*" --b "crusader*" \
        --n 3 --seed 42 --maxsec 300 --mod content/mods/structures.json --json \
        | python3 -c "import sys,json; d=json.load(sys.stdin); print(d['WinsA'], d['Draws'])")
[ "$nofac" = "3 0" ] || { echo "  FACTORY GATE NOT DECISIVE: winsA/draws = $nofac"; exit 1; }
echo "  no factory => defeated (A 3/3)"

# With a factory, production flows and the tank advantage decides it.
fac=$(rts econ --a "usa_power_plant,usa_factory,crusader*" --b "usa_power_plant,usa_factory" \
      --n 3 --seed 42 --maxsec 600 --mod content/mods/structures.json --json \
      | python3 -c "import sys,json; d=json.load(sys.stdin); print(d['WinsA'])")
[ "$fac" = "3" ] || { echo "  PRODUCTION BLOCKED WITH A FACTORY PRESENT: winsA = $fac"; exit 1; }
echo "  factory present => units build and win (A 3/3)"

gate "flags and conditional variants"
# ZH's real upgrade mechanism: an upgrade carries NO effect data, it sets one condition bit
# and a condition-keyed weapon/armor set is selected. Four properties:
#   1. researching a tech grants a same-named team flag
#   2. the flag activates a variant that swaps weapon AND armor class
#   3. the variant is what wins the game — ablate it and the same pack loses
#   4. packs that use no flags are BIT-IDENTICAL (flag state is hashed only when it can vary)
rts lint --mod content/mods/upgrades.json | tail -1

WITH=$(rts econ --a "war_factory,strategy_center,crusader*" --b "war_factory,crusader*" \
       --n 12 --seed 42 --mod content/mods/upgrades.json --json \
       | python3 -c "import sys,json; print(json.load(sys.stdin)['WinsA'])")
[ "$WITH" = "9" ] || { echo "  VARIANT DRIFT: expected winsA=9, got $WITH"; exit 1; }

# Ablation: same pack, variant deleted. If the loadout system were inert this would match.
ABL=$(mktemp -d); trap 'rm -rf "$ABL"' EXIT
python3 - "$ABL" <<'PY'
import json, re, sys
d = json.loads(re.sub(r'^\s*//.*$', '', open('content/mods/upgrades.json').read(), flags=re.M))
d['units']['crusader'].pop('variants')
d['meta']['name'] = 'upgrades-ablated'
open(f"{sys.argv[1]}/novariant.json", "w").write(json.dumps(d))
PY
WITHOUT=$(rts econ --a "war_factory,strategy_center,crusader*" --b "war_factory,crusader*" \
          --n 12 --seed 42 --mod "$ABL/novariant.json" --json \
          | python3 -c "import sys,json; print(json.load(sys.stdin)['WinsA'])")
[ "$WITHOUT" = "0" ] || { echo "  ABLATION FAILED: variant removed but winsA=$WITHOUT (expected 0)"; exit 1; }
echo "  variant decides the game: 9/12 with it, ${WITHOUT}/12 without"

gate "event rules {on, when, do}"
# Our replacement for ZH's ~217 compiled behavior modules. Death first because damage/death
# response is 30.5% of every module instance in the reference corpus (5,083 of 16,685).
# Each effect is proven by ABLATION — same pack, rules deleted — because a win rate on its
# own cannot distinguish "the rule fired" from "the unit was good anyway".
rts lint --mod content/mods/deathrules.json | sed -n '3p'

ABR=$(mktemp -d); trap 'rm -rf "$ABR"' EXIT
python3 - "$ABR" <<'PY'
import json, re, sys
d = json.loads(re.sub(r'^\s*//.*$', '', open('content/mods/deathrules.json').read(), flags=re.M))
for u in d['units'].values(): u.pop('rules', None)
d['meta']['name'] = 'deathrules-ablated'
open(f"{sys.argv[1]}/norules.json", "w").write(json.dumps(d))
PY

# spawn: a tank that leaves two fighting wrecks wins a matchup it otherwise loses.
sp_on=$(rts duel --a scrap_tank --b crusader --n 40 --seed 42 --mod content/mods/deathrules.json --json \
        | python3 -c "import sys,json; print(json.load(sys.stdin)['WinsA'])")
sp_off=$(rts duel --a scrap_tank --b crusader --n 40 --seed 42 --mod "$ABR/norules.json" --json \
        | python3 -c "import sys,json; print(json.load(sys.stdin)['WinsA'])")
[ "$sp_on" -gt "$sp_off" ] || { echo "  SPAWN RULE INERT: $sp_on vs $sp_off"; exit 1; }
echo "  spawn: $sp_on/40 wins with wrecks, $sp_off/40 without"

# grantMoney: salvage shows up as end-of-run net worth, and ONLY in an economy.
gm_on=$(rts econ --a "salvager*" --b "war_factory,crusader*" --n 8 --seed 42 --maxsec 300 \
        --mod content/mods/deathrules.json --json \
        | python3 -c "import sys,json; print(int(json.load(sys.stdin)['avgNetWorthA']))")
gm_off=$(rts econ --a "salvager*" --b "war_factory,crusader*" --n 8 --seed 42 --maxsec 300 \
        --mod "$ABR/norules.json" --json \
        | python3 -c "import sys,json; print(int(json.load(sys.stdin)['avgNetWorthA']))")
[ "$gm_on" -gt "$gm_off" ] || { echo "  GRANTMONEY INERT: $gm_on vs $gm_off"; exit 1; }
echo "  grantMoney: net worth $gm_on with salvage, $gm_off without"

# The stall diagnostic must actually fire on the classic mistake: an order that names a unit
# whose prerequisite it never satisfies. A silent stall makes every result above it a lie.
stall=$(rts econ --a "salvager*" --b "crusader*" --n 1 --seed 42 --maxsec 60 \
        --mod content/mods/deathrules.json | grep -c "STALLED B: 'crusader' needs tech 'war_factory'")
[ "$stall" = "1" ] || { echo "  STALL DIAGNOSTIC MISSING"; exit 1; }
echo "  stalled queues are reported, not silent"

gate "factions are startable, not just rosters"
# A faction that declares startingBuilding/startingUnits/startMoney is PLAYABLE — `rts
# skirmish` starts both sides from their own definitions rather than from a build order
# someone typed. That is the difference the product goal actually needs.
#
# usa_rush is usa_base minus the power plant, plus 2000 cash. The factory draws -5, nothing
# supplies it, production browns out: more money loses to a working grid, every time.
rush=$(rts skirmish --a usa_base --b usa_rush --n 12 --seed 42 --maxsec 400 \
       --mod content/mods/structures.json --json \
       | python3 -c "import sys,json; d=json.load(sys.stdin); print(d['WinsA'], d['WinsB'])")
[ "$rush" = "12 0" ] || { echo "  SKIRMISH DRIFT: winsA/winsB = $rush"; exit 1; }
echo "  power grid beats extra cash: usa_base 12/12 over usa_rush"

# And the loser's stall is NAMED. A silent stall would make the result unreadable.
why=$(rts skirmish --a usa_base --b usa_rush --n 3 --seed 42 --maxsec 400 \
      --mod content/mods/structures.json | grep -c "STALLED B:")
[ "$why" = "1" ] || { echo "  SKIRMISH STALL NOT REPORTED"; exit 1; }
echo "  the loser's stall is reported, not silent"

gate "compile to a Zero Hour mod"
# The pivot: a measured pack becomes additive Data/INI the real engine loads. Verified
# end to end once by booting GeneralsXZH — all 7 files parsed, 0 exceptions, 42/42
# subsystems, 92 Object files loaded (91 retail + ours). This gate keeps it honest.
OUT=$(mktemp -d); trap 'rm -rf "$OUT"' EXIT
rts compile --target zh --out "$OUT" | grep -E "^compiled|objects"

for f in Armor Weapon Locomotor Object CommandButton CommandSet PlayerTemplate; do
  [ -f "$OUT/Data/INI/$f/skeleton_pack.ini" ] || { echo "  MISSING EMIT: $f"; exit 1; }
done
echo "  7 content types emitted into the additive Data/INI/<type>/ scan"

# Emitting Data/INI/Weapon.ini instead of Weapon/<pack>.ini would replace retail's 363
# weapons with our 5 and break the base game. Assert we never regress to that.
[ ! -f "$OUT/Data/INI/Weapon.ini" ] || { echo "  CLOBBERS RETAIL: emitted Weapon.ini"; exit 1; }
[ ! -f "$OUT/Data/Generals.str" ] || { echo "  CLOBBERS RETAIL: emitted Generals.str without --with-strings"; exit 1; }
echo "  retail files untouched (no Weapon.ini, no Generals.str)"

# Their enums are closed and parsed with parseIndexList, so a plausible-sounding value is
# a hard load error. Both of these were invented once and only the round trip caught them.
grep -q "NO_Z_MOTIVE_FORCE" "$OUT/Data/INI/Locomotor/skeleton_pack.ini" \
  || { echo "  BAD ENUM: ZAxisBehavior is not NO_Z_MOTIVE_FORCE"; exit 1; }
! grep -qE "NO_Z_MOTION|CANCELABLE" "$OUT/Data/INI"/*/skeleton_pack.ini \
  || { echo "  INVENTED ENUM VALUE regressed into the emitter"; exit 1; }
echo "  emitted enum values are ones retail actually uses"

gate "target-zh lint — caps, round-trip, divergence"
# Three questions that fail differently: their hard caps (mod will not load), round-trip
# fidelity (the shipped unit is not the measured one), and semantic divergence (it loads,
# plays, and behaves differently from the numbers you tuned against).
rts lint --target zh | grep -E "round-trip checked|cap ·"

# ROUND-TRIP must be clean for every pack. We emit ms and their loader ceils back to 30 Hz
# frames, so the conversion FLOORS: rounding 8 ticks to 267ms ceils back to 9 — a silent
# 12.5% rate-of-fire loss, and precisely the bug that makes their DragonTank read 250 DPS
# and deliver 150. This caught it; keep it catching it.
for m in "" content/mods/artillery.json content/mods/structures.json content/mods/upgrades.json; do
  arg=""; [ -n "$m" ] && arg="--mod $m"
  trips=$(rts lint --target zh $arg | grep -c "^TRIP:" || true)
  [ "$trips" = "0" ] || { echo "  ROUND-TRIP LOSS in '${m:-base}': $trips value(s) change meaning on compile"; exit 1; }
done
echo "  every duration survives ticks -> ms -> ceil() back to the same tick"

# Death rules COMPILE now: damageInRadius -> FireWeaponWhenDeadBehavior (203 retail uses),
# spawn -> ObjectCreationList + CreateObjectDie (566). Verified on the engine: 8 files
# parsed, 0 exceptions, 42/42 subsystems, main loop entered.
DOUT=$(mktemp -d); trap 'rm -rf "$DOUT"' EXIT
rts compile --target zh --out "$DOUT" --mod content/mods/deathrules.json >/dev/null
grep -q "FireWeaponWhenDeadBehavior" "$DOUT/Data/INI/Object/deathrules.ini" \
  || { echo "  damageInRadius did not become a death weapon module"; exit 1; }
grep -q "CreateObjectDie" "$DOUT/Data/INI/Object/deathrules.ini" \
  || { echo "  spawn did not become a CreateObjectDie"; exit 1; }
[ -f "$DOUT/Data/INI/ObjectCreationList/deathrules.ini" ] \
  || { echo "  no ObjectCreationList emitted for spawn effects"; exit 1; }
# SpreadFormation is a BOOL and the distance goes in MinDistanceAFormation. Emitting the
# radius into the bool is a type error their parser rejects — it did, once.
grep -q "SpreadFormation = Yes" "$DOUT/Data/INI/ObjectCreationList/deathrules.ini" \
  || { echo "  SpreadFormation regressed to a non-boolean"; exit 1; }
echo "  death rules compile to real modules (FireWeaponWhenDead, CreateObjectDie + OCL)"

# What still cannot map must be REPORTED, never silent. grantMoney has no faithful
# equivalent: their nearest mechanic pays the KILLER, not the owner.
dv=$(rts lint --target zh --mod content/mods/deathrules.json | grep -c "grantMoney effect(s) are NOT emitted" || true)
[ "$dv" = "1" ] || { echo "  DIVERGENCE NOT REPORTED: grantMoney vanishes without warning"; exit 1; }
echo "  what still cannot map is named, not silently lost"

# Conditional variants COMPILE now. Their shape is ours: a named boolean with a cost, a
# condition-keyed set, and a parameterless module that flips the bit (173 ArmorUpgrade /
# 117 WeaponSetUpgrade / 52 MaxHealthUpgrade in retail). Verified on the engine: 42/42
# subsystems, Data/INI/Upgrade/upgrades.ini parsed, main loop entered.
UOUT=$(mktemp -d); trap 'rm -rf "$DOUT" "$UOUT"' EXIT
rts compile --target zh --out "$UOUT" --mod content/mods/upgrades.json >/dev/null
[ -f "$UOUT/Data/INI/Upgrade/upgrades.ini" ] \
  || { echo "  no Upgrade block emitted for the flag gating a variant"; exit 1; }
grep -q "Conditions = PLAYER_UPGRADE" "$UOUT/Data/INI/Object/upgrades.ini" \
  || { echo "  variant did not become a condition-keyed set"; exit 1; }
grep -q "WeaponSetUpgrade" "$UOUT/Data/INI/Object/upgrades.ini" \
  || { echo "  nothing flips the upgrade bit; the second WeaponSet is dead content"; exit 1; }
# An upgrade with no button cannot be bought, and an unbought upgrade never fires — the
# unit would ship with its base loadout while the harness measured the upgraded one.
grep -q "ButtonBorderType = UPGRADE" "$UOUT/Data/INI/CommandButton/upgrades.ini" \
  || { echo "  upgrade is unpurchasable: no PLAYER_UPGRADE command button"; exit 1; }
# AddMaxHealth is ABSOLUTE where ours is a factor. 480 base x (1.10 - 1) = 48.
grep -q "AddMaxHealth = 48" "$UOUT/Data/INI/Object/upgrades.ini" \
  || { echo "  variant MaxHp factor did not convert to an absolute AddMaxHealth"; exit 1; }
echo "  variants compile to Upgrade + condition sets + *Upgrade modules"

# The ONE-BIT limit is theirs, not ours: an object has a single PLAYER_UPGRADE condition,
# so a second variant has nowhere to live. Retail needed 268 ConflictsWith lines because
# of it. A pack that hits it must be told, not quietly shipped half-applied.
python3 - <<'PY' > "$UOUT/twovar.json"
import json, re
d = json.loads(re.sub(r'^\s*//.*$', '', open('content/mods/upgrades.json').read(), flags=re.M))
d['meta']['name'] = 'twovar'
v = d['units']['crusader']['variants']
v.append({"whenFlags": ["war_factory"], "weapon": "crusader_cannon"})
print(json.dumps(d))
PY
tv=$(rts lint --target zh --mod "$UOUT/twovar.json" | grep -c "only the first compiles" || true)
[ "$tv" = "1" ] || { echo "  ONE-BIT LIMIT NOT REPORTED: extra variants silently never fire"; exit 1; }
echo "  their single PLAYER_UPGRADE bit is reported, not silently overrun"

gate "faction-scoped reference resolution"
FOUT=$(mktemp -d); trap 'rm -rf "$DOUT" "$UOUT" "$FOUT"' EXIT

# A faction patch must produce a COMPLETE clone. This was a live bug: the variant was built
# by a 9-field initializer against a 20-field type, so patching nothing but a war factory's
# cost dropped its KindOf and the building stopped being a structure — it compiled WITH a
# Locomotor. Rules, conditional variants, energy production and birth flags went the same way.
# Structures skip locomotor emission, so counting locomotors is the sharpest available probe.
python3 - > "$FOUT/clone.json" <<'PY'
import json, re
d = json.loads(re.sub(r'^\s*//.*$', '', open('content/mods/structures.json').read(), flags=re.M))
d['meta']['name'] = 'clone'
d['factions']['usa_probe'] = {"extends": "usa_base", "modify": {"usa_factory": {"cost": 1900}}}
print(json.dumps(d))
PY
rts compile --target zh --out "$FOUT/clone" --mod "$FOUT/clone.json" >/dev/null
grep -q "usa_probe_usa_factoryLoco" "$FOUT/clone/Data/INI/Locomotor/clone.ini" \
  && { echo "  A PATCHED STRUCTURE LOST ITS KindOf: the factory compiled with a Locomotor"; exit 1; }
echo "  a faction patch clones every field, not the nine someone remembered"

# Retail's dominant defect, in our model: a fork whose reference still points at the base.
# 64 of their 88 bad references are this shape (shared OCL, forked payload). A faction that
# forks BOTH a tank and the wreck it leaves must get its own wreck.
python3 - > "$FOUT/scope.json" <<'PY'
import json, re
d = json.loads(re.sub(r'^\s*//.*$', '', open('content/mods/deathrules.json').read(), flags=re.M))
d['meta']['name'] = 'scope'
d['factions']['gla_fork'] = {"extends": "gla_scrap", "add": ["wreck"],
                             "modify": {"scrap_tank": {"cost": 850}, "wreck": {"cost": 2}}}
print(json.dumps(d))
PY
rts compile --target zh --out "$FOUT/scope" --mod "$FOUT/scope.json" >/dev/null
grep -A2 "OCL_scope_gla_fork_scrap_tank_Death" "$FOUT/scope/Data/INI/ObjectCreationList/scope.ini" \
  | grep -q "ObjectNames = scope_gla_fork_wreck" \
  || { echo "  A FORKED UNIT SPAWNED THE BASE PROTOTYPE: spawn did not resolve faction-scoped"; exit 1; }
# ...and the base must be unaffected, or "scoping" is just a global rewrite.
grep -A2 "OCL_scope_scrap_tank_Death" "$FOUT/scope/Data/INI/ObjectCreationList/scope.ini" \
  | grep -q "ObjectNames = scope_wreck" \
  || { echo "  scoping leaked into the base prototype"; exit 1; }
echo "  a variant's spawn resolves through its own faction; the base is untouched"

# Same for object prerequisites: the forked crusader is gated on the forked factory.
python3 - > "$FOUT/prereq.json" <<'PY'
import json, re
d = json.loads(re.sub(r'^\s*//.*$', '', open('content/mods/structures.json').read(), flags=re.M))
d['meta']['name'] = 'prereq'
d['factions']['usa_fork'] = {"extends": "usa_base",
                             "modify": {"usa_factory": {"cost": 1900}, "crusader": {"cost": 880}}}
print(json.dumps(d))
PY
rts compile --target zh --out "$FOUT/prereq" --mod "$FOUT/prereq.json" >/dev/null
# Emitted INI is CRLF (their parser is a 2003 Windows line reader), so strip CR before
# anchoring — a bare /^End$/ never matches and the range silently reads empty.
tr -d '\r' < "$FOUT/prereq/Data/INI/Object/prereq.ini" \
  | awk '/^Object prereq_usa_fork_crusader$/,/^End$/' \
  | grep -q "Object = prereq_usa_fork_usa_factory" \
  || { echo "  A FORKED UNIT REQUIRED THE BASE BUILDING: prerequisite did not resolve scoped"; exit 1; }
echo "  a variant's prerequisites resolve through its own faction"

# What resolution CANNOT fix must be reported. A shared prototype is used by several
# factions, so its reference array cannot mean different things to each — the position
# EA's shared Paradrop OCL is in. Fork the factory but not what depends on it and the
# dependency still names the base object.
sc=$(rts lint --mod "$FOUT/clone.json" | grep -c "shared unit 'crusader' requires 'usa_factory'" || true)
[ "$sc" = "1" ] || { echo "  SHARED-REFERRER DEFECT NOT REPORTED: the retail bug class ships silently"; exit 1; }
echo "  a shared prototype referencing a forked one is named, not silently mis-resolved"

gate "garrison + capture"
# GARRISONABLE is the only terrain-shaped combat modifier in all of Zero Hour and it needs
# no geometry at all. Occupants cannot be targeted; damage reaches them only as spill from a
# ClearsGarrison weapon hitting the HOST — 17 of retail's 363 weapons, 4.7% of the arsenal.
#
# The evidence is a CONTROLLED experiment, not a win rate: raider and dragon_tank are
# identical in cost, hitpoints, speed, damage, range, cooldown and damage type, and differ
# in exactly one boolean. Anything that separates them is that boolean.
rts lint --mod content/mods/garrison.json | tail -1

hold_wins() { rts hold --host bunker --holder ranger --attacker "$1" --n 40 --seed 42 $2 \
              --mod content/mods/garrison.json --json \
              | python3 -c "import sys,json; print(json.load(sys.stdin)['WinsA'])"; }

on_plain=$(hold_wins raider "")
off_plain=$(hold_wins raider --no-garrison)
[ "$on_plain" = "40" ] || { echo "  GARRISON INERT: held position won only $on_plain/40 vs an ordinary weapon"; exit 1; }
[ "$off_plain" = "0" ]  || { echo "  ABLATION FAILED: same units unhoused still won $off_plain/40"; exit 1; }
echo "  a held building decides it: $on_plain/40 garrisoned, $off_plain/40 not"

on_clear=$(hold_wins dragon_tank "")
[ "$on_clear" = "0" ] || { echo "  CLEARS-GARRISON INERT: flame attacker still lost, defenders won $on_clear/40"; exit 1; }
echo "  and one boolean undoes it: clearsGarrison attacker wins 40/40 against the same position"

# Capture moves both the building and the income it pays. Their AutoDepositUpdate pays the
# CURRENT owner, so the money following the flag is the whole point.
cap=$(rts hold --host bunker --holder ranger --attacker raider --prize oil_derrick \
      --n 3 --seed 42 --mod content/mods/garrison.json --json \
      | python3 -c "import sys,json; d=json.load(sys.stdin); print(d['PrizeOwner'], int(d['MoneyA']), int(d['MoneyB']))")
[ "$cap" = "1 0 200" ] || { echo "  CAPTURE/DEPOSIT DRIFT: (owner moneyA moneyB) = $cap, expected '1 0 200'"; exit 1; }
echo "  a captured derrick changes hands and its deposit follows the new owner"

# GARRISONABLE reads like a KindOf and is not one: their bit table has only NO_GARRISON,
# STEALTH_GARRISON and GARRISONABLE_UNTIL_DESTROYED. Emitting it was a hard load error that
# a grep missed, because it is a substring of the real name. Never let it back in.
GOUT=$(mktemp -d); trap 'rm -rf "$DOUT" "$UOUT" "$FOUT" "$GOUT"' EXIT
rts compile --target zh --out "$GOUT" --mod content/mods/garrison.json >/dev/null
grep -E "KindOf.*\bGARRISONABLE\b" "$GOUT/Data/INI/Object/garrison.ini" \
  && { echo "  INVENTED ENUM: GARRISONABLE is not a real KindOf and will not load"; exit 1; }
grep -q "GarrisonContain" "$GOUT/Data/INI/Object/garrison.ini" \
  || { echo "  garrisonCapacity did not become a GarrisonContain module"; exit 1; }
grep -q "AllowAttackGarrisonedBldgs = Yes" "$GOUT/Data/INI/Weapon/garrison.ini" \
  || { echo "  clearsGarrison did not reach the weapon"; exit 1; }
grep -q "AutoDepositUpdate" "$GOUT/Data/INI/Object/garrison.ini" \
  || { echo "  depositAmount did not become an AutoDepositUpdate"; exit 1; }
echo "  compiles to GarrisonContain + AutoDepositUpdate + AllowAttackGarrisonedBldgs"

gate "sciences — a second currency earned by fighting"
# Skill points come from KILLS and from nothing else; no economy converts into them. ZH's
# whole ladder is five Rank.ini blocks: 0/800/1500/2500/5000 needed, 1/1/1/1/3 granted, so
# seven points for a game against 13-20 purchasable sciences per faction. Every purchasable
# retail science costs exactly 1 — the tree's shape is entirely in its prerequisites.
rts lint --mod content/mods/sciences.json | tail -1

# Prerequisites PRUNE the space; that is what makes brute-forcing subsets wrong. Three
# sciences give 8 subsets, but marksmanship2 requires marksmanship1, so 2 are unreachable.
legal=$(rts science-matrix --mod content/mods/sciences.json --n 1 --seed 42 --json \
        | python3 -c "import sys,json; print(len(json.load(sys.stdin)['rows']))")
[ "$legal" = "6" ] || { echo "  SCIENCE TREE DRIFT: $legal legal loadouts, expected 6 of 8 subsets"; exit 1; }
echo "  prerequisites prune the space: 6 legal loadouts of 8 subsets"

# The matrix plays every loadout from BOTH sides and counts the science-owner's wins. That
# swap is load-bearing: ascending unit index makes team 0 resolve first, and an EMPTY loadout
# measured 11/12 for team 0 before it. A 50% baseline is what makes the rest readable.
rows=$(rts science-matrix --mod content/mods/sciences.json --n 12 --seed 42 --json \
       | python3 -c "
import sys,json
d=json.load(sys.stdin)
w={r['Sciences']: r['Wins'] for r in d['rows']}
print(w['(none)'], w['marksmanship1+marksmanship2'])")
base=$(echo "$rows" | cut -d' ' -f1); best=$(echo "$rows" | cut -d' ' -f2)
[ "$base" = "12" ] || { echo "  SIDE BIAS NOT CANCELLED: empty loadout won $base/24, expected 12"; exit 1; }
[ "$best" -gt "$base" ] || { echo "  SCIENCES INERT: best loadout $best/24 vs baseline $base/24"; exit 1; }
echo "  mirror baseline is exactly $base/24; the best loadout reaches $best/24"

# Sciences reach the battlefield through the flag/variant machinery that already existed —
# no new effect path. Ablate the variants and the same purchases must stop mattering.
SOUT=$(mktemp -d); trap 'rm -rf "$DOUT" "$UOUT" "$FOUT" "$GOUT" "$SOUT"' EXIT
python3 - > "$SOUT/flat.json" <<'PY2'
import json, re
d = json.loads(re.sub(r'^\s*//.*$', '', open('content/mods/sciences.json').read(), flags=re.M))
d['meta']['name'] = 'sciences-ablated'
d['units']['crusader'].pop('variants')
print(json.dumps(d))
PY2
flat=$(rts science-matrix --mod "$SOUT/flat.json" --n 12 --seed 42 --json \
       | python3 -c "
import sys,json
print(max(r['Wins'] for r in json.load(sys.stdin)['rows']))")
[ "$flat" = "12" ] || { echo "  ABLATION FAILED: variants removed but a loadout still won $flat/24"; exit 1; }
echo "  ablate the variants and every loadout falls back to $flat/24 — the flag seam is the whole effect"

# Science blocks compile additively. Confirmed against the RUNNING engine, not the source:
# EA's GameEngine.cpp passes no directory to TheScienceStore, but the build we target logs
# loadDirectory('Data\INI\Science'). Schema authority is their source; load behaviour is not.
rts compile --target zh --out "$SOUT/zh" --mod content/mods/sciences.json >/dev/null
grep -q "SciencePurchasePointCost = 1" "$SOUT/zh/Data/INI/Science/sciences.ini" \
  || { echo "  sciences did not compile to Science blocks"; exit 1; }
grep -q "PrerequisiteSciences = sciences_marksmanship1" "$SOUT/zh/Data/INI/Science/sciences.ini" \
  || { echo "  science prerequisites were not carried into the emitted tree"; exit 1; }
# What CANNOT be additive must be reported: Rank blocks are numbered, not named.
rl=$(rts lint --target zh --mod content/mods/sciences.json | grep -c "rank ladder is NOT emitted" || true)
[ "$rl" = "1" ] || { echo "  RANK LADDER DIVERGENCE NOT REPORTED"; exit 1; }
echo "  Science blocks compile additively; the numbered Rank ladder is reported, not overwritten"

gate "spatial index is a PURE accelerator"
# Every radius query used to be a full scan, so tick cost was O(n^2) — and this product's
# value is BATCH measurement, where a matrix is pairs x runs x ticks. The grid must change
# the COST and nothing else, so the gate is equivalence, not speed: same seed, same content,
# grid vs --brute, identical final state hash. A broad phase that changes an answer is a
# desync, and pinned hashes alone would not catch it at scales they never reach.
for budget in 3600 30000 300000; do
  gh=$(rts duel --a ranger --b technical --n 1 --seed 42 --budget $budget --json \
       | sed -n 's/.*"lastFinalHash": "\(.*\)".*/\1/p')
  bh=$(rts duel --a ranger --b technical --n 1 --seed 42 --budget $budget --json --brute \
       | sed -n 's/.*"lastFinalHash": "\(.*\)".*/\1/p')
  [ "$gh" = "$bh" ] || { echo "  BROAD PHASE CHANGED AN ANSWER at budget $budget: $gh != $bh"; exit 1; }
done
echo "  grid == brute force at 3 scales (~24, ~200, ~2000 units)"

# Splash is the case that would fail silently: damage is applied in visit order, deaths feed
# the rule cascade, and the cascade draws from a Pcg32 stream. An unsorted broad phase would
# reorder RNG draws while still hitting the right units.
sg=$(rts duel --a katyusha --b ranger --n 4 --seed 7 --budget 12000 --json \
     --mod content/mods/artillery.json | sed -n 's/.*"lastFinalHash": "\(.*\)".*/\1/p')
sb=$(rts duel --a katyusha --b ranger --n 4 --seed 7 --budget 12000 --json --brute \
     --mod content/mods/artillery.json | sed -n 's/.*"lastFinalHash": "\(.*\)".*/\1/p')
[ "$sg" = "$sb" ] || { echo "  SPLASH ORDER DIVERGED: $sg != $sb"; exit 1; }
echo "  splash order preserved under the grid (candidates come back ascending)"

# And it has to actually be faster, or it is pure risk. Wall clock is noisy, so assert only
# a large margin at a scale where the quadratic term dominates.
gt=$( { time rts duel --a ranger --b technical --n 1 --seed 42 --budget 300000 >/dev/null; } 2>&1 \
      | grep real | sed 's/.*m\([0-9.]*\)s/\1/')
bt=$( { time rts duel --a ranger --b technical --n 1 --seed 42 --budget 300000 --brute >/dev/null; } 2>&1 \
      | grep real | sed 's/.*m\([0-9.]*\)s/\1/')
python3 -c "
import sys
g, b = float('$gt'), float('$bt')
if g * 2.0 > b:
    print(f'  BROAD PHASE NOT PAYING: grid {g:.2f}s vs brute {b:.2f}s at ~2000 units'); sys.exit(1)
print(f'  ~2000 units: {g:.2f}s with the grid vs {b:.2f}s without ({b/g:.1f}x)')
"

gate "passability — terrain that movement must respect"
# Chokepoints, flanking and water are EMERGENT from a passability grid; none of them is a
# content type. The gate is therefore mostly about consequences — does the same army, on the
# same seed, measure differently because of the shape of the ground — plus the two structural
# claims that are silent when wrong.
PAS=$(mktemp -d)
python3 - "$PAS" <<'PY8'
import sys, json
d = sys.argv[1]

def pack(name, rows, cell=2.0, outside="clear", legend=None, extra=None):
    p = {"meta": {"name": name, "version": 1},
         "map": {"cellSize": cell, "outside": outside,
                 "legend": legend or {".": "clear", "#": "impassable"}, "rows": rows}}
    if extra: p.update(extra)
    json.dump(p, open(f"{d}/{name}.json", "w"))

W = H = 48
# An inert map: every cell crossable. Must be a no-op, hash for hash.
pack("flat", ["." * W] * H)
# A wall 12 world units thick — WIDER than any weapon in the base pack can reach. A thin wall
# measures as nothing because both sides simply shoot over it: terrain blocks movement, and
# there is no line-of-sight model. That mistake made an earlier version of this gate pass
# against a barrier that did nothing.
def wall(gap):
    return [''.join('#' if (21 <= x < 27 and y not in gap) else '.' for x in range(W))
            for y in range(H)]
pack("solid", wall(range(0, 0)))
pack("gated", wall(range(5, 9)))          # gate off the spawn axis, so it is a real detour

# Corner cutting, at a cell size coarse enough that the barrier cannot be shot across.
# Every crossing of a one-cell diagonal is a squeeze between two blocked cells; a pathfinder
# that allows it walks through a wall with no gap in it.
N = 8
pack("pinch", [''.join('#' if x == y else '.' for x in range(N)) for y in range(N)],
     cell=16.0, outside="impassable")
open_rows = [list(r) for r in [''.join('#' if x == y else '.' for x in range(N)) for y in range(N)]]
open_rows[4][4] = '.'
pack("pinch_open", [''.join(r) for r in open_rows], cell=16.0, outside="impassable")

# Surfaces are a MASK, so the same river is a wall, a road or nothing depending on the unit.
river = [''.join('~' if 21 <= x < 27 else '.' for x in range(W)) for y in range(H)]
units = {}
for nm, surf in [("walker", ["ground"]), ("hover", ["ground", "water"]), ("flyer", ["air"])]:
    units[nm] = {"faction": "usa", "cost": 900, "buildSeconds": 10,
                 "kindOf": ["VEHICLE", "SELECTABLE", "CAN_ATTACK"],
                 "components": {"Health": {"max": 800, "armorClass": "heavy_vehicle"},
                                "Mobile": {"speed": 5.0, "surfaces": surf},
                                "WeaponBearer": {"weapon": "crusader_cannon"}}}
pack("river", river, legend={".": "clear", "~": "water"},
     extra={"units": units,
            "zh": {"models": {k: "AVLeopard" for k in units}}})

# Malformed maps. Each of these loads happily if unchecked and puts the walls somewhere the
# author did not draw them, which no test but a screenshot would ever catch.
pack("badcell", ["." * 4] * 4, cell=0.3)
json.dump({"meta": {"name": "ragged", "version": 1},
           "map": {"cellSize": 2.0, "legend": {".": "clear"}, "rows": ["....", "..."]}},
          open(f"{d}/ragged.json", "w"))
json.dump({"meta": {"name": "unlisted", "version": 1},
           "map": {"cellSize": 2.0, "legend": {".": "clear"}, "rows": ["..X.", "...."]}},
          open(f"{d}/unlisted.json", "w"))
PY8

hash_of() { rts duel --a crusader --b battlemaster --n 5 --seed 42 --json "$@" \
            | sed -n 's/.*"lastFinalHash": "\(.*\)".*/\1/p'; }
outcome() { rts duel --a "$1" --b "$2" --n 3 --seed 42 --mod "$3" | sed -n '2p'; }
secs() { rts duel --a crusader --b battlemaster --n 20 --seed 42 "$@" \
         | sed -n 's/.*battle length: \([0-9.]*\)s.*/\1/p'; }

# --- 1. Opt-in by CONSEQUENCE, not by presence. --------------------------------------
# A map every unit can cross everywhere changes no movement, so it must change no hash.
# The alternative — any map at all re-baselines every replay — would make "I drew some
# terrain and all my pinned measurements broke" true even when the terrain does nothing.
[ "$(hash_of)" = "$(hash_of --mod "$PAS/flat.json")" ] \
  || { echo "  AN INERT MAP MOVED A HASH: $(hash_of) -> $(hash_of --mod "$PAS/flat.json")"
       rm -rf "$PAS"; exit 1; }
rts lint --mod "$PAS/flat.json" | grep -q "pathing=False" \
  || { echo "  an all-clear map should not report pathing"; rm -rf "$PAS"; exit 1; }
echo "  an all-clear map is a no-op: identical final hash, pathing off"

# --- 2. A barrier must actually stop an army. ----------------------------------------
case "$(outcome crusader battlemaster "$PAS/solid.json")" in
  *"draws: 3"*) echo "  a solid barrier is solid: the same armies never meet" ;;
  *) echo "  A SOLID BARRIER DID NOT BLOCK — pathing is advisory, not enforced"
     outcome crusader battlemaster "$PAS/solid.json"; rm -rf "$PAS"; exit 1 ;;
esac

# --- 3. A gate in that barrier must change the MEASUREMENT, not just the hash. -------
open_s=$(secs); gated_s=$(secs --mod "$PAS/gated.json")
python3 -c "
import sys
o, g = float('$open_s'), float('$gated_s')
if g < o * 1.3:
    print(f'  A CHOKEPOINT COST NOTHING: {o}s open vs {g}s gated'); sys.exit(1)
print(f'  a chokepoint is emergent, not authored: {o}s open -> {g}s through one gate')
"

# --- 4. Corners may not be cut. ------------------------------------------------------
# Both maps are one-cell diagonals; the second has a single cell removed, which is the only
# difference between "no way through" and "an orthogonal doorway".
case "$(outcome crusader battlemaster "$PAS/pinch.json")" in
  *"draws: 3"*) : ;;
  *) echo "  CORNER CUTTING: units squeezed diagonally between two blocked cells"
     rm -rf "$PAS"; exit 1 ;;
esac
case "$(outcome crusader battlemaster "$PAS/pinch_open.json")" in
  *"draws: 0"*) echo "  diagonal squeezes refused, and one removed cell reopens the route" ;;
  *) echo "  CONTROL FAILED: the barrier blocks even with a real gap in it"
     rm -rf "$PAS"; exit 1 ;;
esac

# --- 5. Surfaces are a mask: one river, three answers. --------------------------------
case "$(outcome walker battlemaster "$PAS/river.json")" in
  *"draws: 3"*) : ;;
  *) echo "  WATER DID NOT STOP A GROUND UNIT"; rm -rf "$PAS"; exit 1 ;;
esac
for u in hover flyer; do
  case "$(outcome $u battlemaster "$PAS/river.json")" in
    *"$u: 3 wins"*) : ;;
    *) echo "  '$u' FAILED TO CROSS THE RIVER"; outcome $u battlemaster "$PAS/river.json"
       rm -rf "$PAS"; exit 1 ;;
  esac
done
echo "  one river, three answers: ground stopped, hovercraft crosses, air ignores it"

# --- 6. Terrain must not break determinism or the broad phase. ------------------------
rts replay --a crusader --b battlemaster --seed 7 --mod "$PAS/gated.json" | grep -q "DETERMINISM OK" \
  || { echo "  REPLAY DIVERGED WITH TERRAIN"; rm -rf "$PAS"; exit 1; }
g=$(hash_of --mod "$PAS/gated.json"); b=$(hash_of --mod "$PAS/gated.json" --brute)
[ "$g" = "$b" ] || { echo "  BROAD PHASE CHANGED AN ANSWER WITH TERRAIN: $g != $b"; rm -rf "$PAS"; exit 1; }
echo "  replay bit-identical with terrain, and grid == brute ($g)"

# --- 7. A malformed map is an ERROR, never a best guess. ------------------------------
# Every one of these still "loads" if unchecked, and simply puts the walls somewhere else.
# Capture before matching: these packs are SUPPOSED to fail, so lint exits non-zero and
# `set -o pipefail` would kill the run on a passing assertion.
check_err() {
  local out; out=$(rts lint --mod "$1" 2>&1 || true)
  case "$out" in
    *"$2"*) : ;;
    *) echo "  MALFORMED MAP ACCEPTED ($1 should report: $2)"; echo "$out" | tail -3
       rm -rf "$PAS"; exit 1 ;;
  esac
}
check_err "$PAS/badcell.json" "not a power of two"
check_err "$PAS/ragged.json" "ragged map shifts every cell"
check_err "$PAS/unlisted.json" "which the legend does not define"
echo "  non-power-of-two cellSize, ragged rows and unlisted characters are all rejected"

# --- 8. Surfaces cross over to ZH; the map does not, and says so. ---------------------
rts compile --target zh --out "$PAS/zh" --mod "$PAS/river.json" >/dev/null
# No end-of-line anchor: emitted INI is CRLF, because that is what their files are, so a
# "$" here matches nothing and the assertion silently inverts into always-failing.
hover_loco=$(grep -A1 "^Locomotor river_hoverLoco" "$PAS/zh/Data/INI/Locomotor/river.ini" || true)
case "$hover_loco" in
  *"Surfaces = GROUND WATER"*) : ;;
  *) echo "  HOVERCRAFT DID NOT CROSS OVER: emitted Surfaces is wrong"
     echo "$hover_loco"; rm -rf "$PAS"; exit 1 ;;
esac
# Water has no height analogue — it needs a PolygonTrigger with isWater, which the map
# writer does not emit — so it must be REPORTED as flattened rather than quietly shipped as
# ground that a hovercraft has no reason to prefer.
zh_lint=$(rts lint --target zh --mod "$PAS/river.json" 2>&1 || true)
case "$zh_lint" in
  *"water/rubble cell"*"OPEN GROUND"*) : ;;
  *) echo "  FLATTENED WATER IS NOT REPORTED AS A DIVERGENCE"; echo "$zh_lint"
     rm -rf "$PAS"; exit 1 ;;
esac
echo "  Surfaces cross over verbatim; water flattens and lint says so"
rm -rf "$PAS"
gate "overrides compile to map.ini, and only there"
# Modifying a unit that ALREADY EXISTS in the target game is a different problem from
# authoring one, and the rules were each bought with a crash. The author states intent; the
# compiler picks the file and the mechanism. This gate asserts it keeps picking correctly.
OVO=$(mktemp -d); trap 'rm -rf "$DOUT" "$UOUT" "$FOUT" "$GOUT" "$SOUT" "$OVO"' EXIT
rts lint --mod content/mods/retail-override.json | tail -1
rts compile --target zh --out "$OVO/base" --mod content/mods/retail-override.json >/dev/null

[ -f "$OVO/base/map.ini" ] || { echo "  no map.ini emitted for the override"; exit 1; }

# RULE: nothing NEW may be declared in map.ini. A new name there is marked as an override and
# LocomotorStore::reset() erases without reassigning its iterator -- the game HANGS on match
# teardown. So map.ini may contain Object blocks and nothing else at top level.
stray=$(tr -d '\r' < "$OVO/base/map.ini" | grep -E "^[A-Za-z]" | grep -vE "^(Object |End$)" | head -1 || true)
[ -z "$stray" ] || { echo "  NEW DECLARATION IN map.ini: '$stray' — this hangs the game on match exit"; exit 1; }
echo "  map.ini contains overrides only; no new name can hang match teardown"

# RULE: a shared leaf is never patched in place. Speed must appear as a NEW locomotor in
# Data/INI plus a repoint in map.ini -- patching theirs makes it unreachable and inert.
grep -q "^Locomotor override_buff_rangerLoco" "$OVO/base/Data/INI/Locomotor/override_overrides.ini" \
  || { echo "  speed did not become a NEW locomotor in Data/INI"; exit 1; }
tr -d '\r' < "$OVO/base/map.ini" | grep -q "Locomotor = SET_NORMAL override_buff_rangerLoco" \
  || { echo "  map.ini did not repoint the object at the new locomotor"; exit 1; }
echo "  speed = new leaf in Data/INI + repoint in map.ini, never an in-place patch"

# RULE: ReplaceModule names the module it replaces and gives the replacement a NEW tag.
tr -d '\r' < "$OVO/base/map.ini" | grep -q "ReplaceModule ModuleTag_01" \
  || { echo "  model swap did not use ReplaceModule"; exit 1; }
tr -d '\r' < "$OVO/base/map.ini" | grep -q "Draw = W3DModelDraw ModuleTag_01_Override" \
  || { echo "  ReplaceModule did not give the replacement a new unique tag"; exit 1; }
echo "  model swap uses ReplaceModule with a new unique tag"

# RULE: scope is explicit, and 'all' fans out across the target's forks. Their generals are
# FORKS, so one edit has to reach four objects -- guessing that is what put 88 bad references
# into EA's own shipping content.
python3 - > "$OVO/all.json" <<'PY2'
import json, re
d = json.loads(re.sub(r'^\s*//.*$', '', open('content/mods/retail-override.json').read(), flags=re.M))
d['meta']['name'] = 'allscope'
d['overrides']['buff_ranger']['scope'] = 'all'
print(json.dumps(d))
PY2
rts compile --target zh --out "$OVO/all" --mod "$OVO/all.json" >/dev/null
n=$(tr -d '\r' < "$OVO/all/map.ini" | grep -c "^Object ")
[ "$n" = "4" ] || { echo "  FORK FAN-OUT WRONG: scope=all touched $n objects, expected 4"; exit 1; }
b=$(tr -d '\r' < "$OVO/base/map.ini" | grep -c "^Object ")
[ "$b" = "1" ] || { echo "  scope=base touched $b objects, expected 1"; exit 1; }
echo "  scope: base touches 1 object, all fans out to 4 forks from one declaration"

# The weapon must be OURS. Naming the target game's own weapon is the inert-gun bug, so it
# has to fail at lint rather than in someone's match.
python3 - > "$OVO/theirs.json" <<'PY3'
import json, re
d = json.loads(re.sub(r'^\s*//.*$', '', open('content/mods/retail-override.json').read(), flags=re.M))
d['meta']['name'] = 'theirweapon'
d['overrides']['buff_ranger']['weapon'] = 'RangerAdvancedCombatRifle'
print(json.dumps(d))
PY3
rts lint --mod "$OVO/theirs.json" >/dev/null 2>&1 \
  && { echo "  LINT ACCEPTED A TARGET-GAME WEAPON: that compiles to an inert gun"; exit 1; }
echo "  naming the target's own weapon is refused at lint, not discovered in a match"

gate "an emitted object is PLAYABLE, not merely parseable"
# Every check here corresponds to a silent in-game failure found by playing the compiled
# output: the file parsed, the engine booted 42/42, and something just never happened.
# A field-diff against AmericaWarFactory and AmericaTankCrusader is what surfaced them.
POUT=$(mktemp -d); trap 'rm -rf "$DOUT" "$UOUT" "$FOUT" "$GOUT" "$SOUT" "$OVO" "$POUT"' EXIT
rts compile --target zh --out "$POUT" --mod content/mods/demo-attach.json >/dev/null
OBJ="$POUT/Data/INI/Object/demo.ini"

# A factory needs BOTH production modules or the menu works and nothing is ever built.
for m in ProductionUpdate DefaultProductionExitUpdate; do
  tr -d '\r' < "$OBJ" | awk '/^Object demo_hellfire_works$/,/^End$/' | grep -q "Behavior = $m" \
    || { echo "  factory is missing $m — buttons would render and build nothing"; exit 1; }
done
# Zero doors: with any other value the queue waits on a model condition state we never emit.
tr -d '\r' < "$OBJ" | awk '/^Object demo_hellfire_works$/,/^End$/' | grep -q "NumDoorAnimations = 0" \
  || { echo "  NumDoorAnimations != 0 — production stalls on a door animation that does not exist"; exit 1; }
echo "  factory: production modules present, no door the model cannot open"

# A mobile object without PhysicsBehavior is created, renders, and can neither move nor be
# selected. Every retail mobile object has one.
tr -d '\r' < "$OBJ" | awk '/^Object demo_hellhound$/,/^End$/' | grep -q "Behavior = PhysicsBehavior" \
  || { echo "  unit is missing PhysicsBehavior — it would spawn inert and unselectable"; exit 1; }
echo "  unit: physics present, so the locomotor can actually drive it"

# Geometry must be DERIVED FROM THE MESH, never an invented constant — a 22-radius box against
# the 53x60 ABWarFact mesh put every produced unit inside the visible building. Assert the
# relationship, not a number: an earlier version of this check hardcoded 53 and broke the
# moment the demo switched meshes, which is a test asserting the wrong thing.
python3 - "$OBJ" "$POUT/../.." <<'PY4'
import json, os, re, sys
obj = open(sys.argv[1], encoding="latin-1").read().replace("\r", "")
blk = re.search(r"^Object demo_hellfire_works$(.*?)^End$", obj, re.S | re.M).group(1)
model = re.search(r"Model\s*=\s*(\S+)", blk).group(1)
radius = re.search(r"GeometryMajorRadius\s*=\s*(\S+)", blk).group(1)
rally = re.search(r"NaturalRallyPoint\s*=\s*X:(\S+)", blk).group(1)
prof = json.load(open("reference/art-profiles.json"))["models"]
if model not in prof:
    print(f"  structure adopts unprofiled mesh '{model}'"); sys.exit(1)
want = prof[model]["majorRadius"]
if float(radius) != float(want):
    print(f"  geometry not derived: emitted {radius}, mesh '{model}' measures {want}"); sys.exit(1)
# Retail's own comment: NaturalRallyPointX must match GeometryMajorRadius, or a produced unit
# is released somewhere other than the edge of the building it came out of.
if float(rally) != float(radius):
    print(f"  rally point {rally} does not match geometry {radius}"); sys.exit(1)
print(f"  geometry and rally point both derived from mesh '{model}' ({radius})")
PY4

# Presentation that is mandatory rather than cosmetic.
grep -q "^ControlBarScheme hellfire8x6" <(tr -d '\r' < "$POUT/Data/INI/ControlBarScheme/demo.ini") \
  || { echo "  no ControlBarScheme for the new Side — the faction has no command bar"; exit 1; }
tr -d '\r' < "$OBJ" | awk '/^Object demo_hellhound$/,/^End$/' | grep -q "SelectPortrait" \
  || { echo "  no SelectPortrait — the control bar shows an empty tile"; exit 1; }
echo "  a new Side gets a command bar, and objects get portraits"

# An adopted mesh carries a CONTRACT — dimensions, weapon bones, muzzle-flash sub-object,
# turret — and every clause of it fails silently. The profile belongs to the MESH, so it is
# measured once from the objects that already use it rather than hand-copied per unit.
# reference/ is generated locally and gitignored, so skip when it is absent.
if [ -f reference/art-profiles.json ]; then
  # Nothing we ship may adopt an UNPROFILED mesh. Retail's 2,928 unreferenced models are
  # adoptable precisely because no object uses them, which is the same reason there is
  # nothing to measure: free of conflicts and free of guidance are the same fact.
  for m in "" content/mods/demo-attach.json content/mods/garrison.json; do
    arg=""; [ -n "$m" ] && arg="--mod $m"
    u=$(rts compile --target zh --out "$POUT/prof" $arg 2>&1 | grep -c "has no measured profile" || true)
    [ "$u" = "0" ] || { echo "  '${m:-base}' adopts $u mesh(es) with no measured profile"; exit 1; }
  done
  echo "  every adopted mesh is one retail uses, so its contract is measured not guessed"

  # And the contract must actually reach the output: the Crusader mesh is turreted, fires
  # from TurretMS and flashes TurretFX. None of that is authored in the pack any more.
  rts compile --target zh --out "$POUT/derived" --mod content/mods/demo-attach.json >/dev/null
  D="$POUT/derived/Data/INI/Object/demo.ini"
  tr -d '\r' < "$D" | awk '/^Object demo_hellhound$/,/^End$/' | grep -q "WeaponMuzzleFlash = PRIMARY TurretFX" \
    || { echo "  muzzle flash not derived from the mesh profile — expect a permanent flame"; exit 1; }
  tr -d '\r' < "$D" | awk '/^Object demo_hellhound$/,/^End$/' | grep -q "ControlledWeaponSlots = PRIMARY" \
    || { echo "  turret not derived from the mesh profile — the unit would never fire"; exit 1; }
  # The unit's rig is the interesting derivation here; the structure's geometry is asserted
  # against the profile a few lines below, by relationship rather than by constant.
  tr -d '\r' < "$D" | awk '/^Object demo_hellhound$/,/^End$/' | grep -q "WeaponLaunchBone = PRIMARY" \
    || { echo "  weapon bones not derived from the mesh profile"; exit 1; }
  echo "  turret, bones, flash and geometry all derived from the mesh, none hand-authored"
else
  echo "  (skipped: reference/art-profiles.json absent — run tools/zhasset artprofile)"
fi

echo
echo "== regression: FILE ORDER must not change resolved content =="
# Companion to the prototype-renumbering guard, generative for the same reason. The weapon and
# veterancy tables were built by iterating a JSON dictionary, so their order came from DOCUMENT
# ORDER: moving two weapons in a file renumbered every index after them.
#
# The FIRST version of this check compared duel hashes and passed even with the bug present —
# sim state never folds a weapon index, so duels cannot see it. That is the same blind spot
# that let ordinal prototype identity survive. The observable that DOES see it is the compiled
# output, so compare that instead.
#
# Note contentHash legitimately DIFFERS between the two: it folds the pack's bytes, and the
# reversed file genuinely is different bytes. Provenance tracks the file; resolution must not.
ROUT=$(mktemp -d); trap 'rm -rf "$ROUT"' EXIT
python3 - "$ROUT" <<'PY2'
import json, re, sys
src = re.sub(r'^\s*//.*$', '', open('content/game.json').read(), flags=re.M)
d = json.loads(src)
# Reverse the insertion order of every keyed table: semantically identical, textually as
# different as it can be.
for key in ("weapons", "veterancyTracks", "units", "factions"):
    if key in d and isinstance(d[key], dict):
        d[key] = {k: d[key][k] for k in reversed(list(d[key]))}
json.dump(d, open(f"{sys.argv[1]}/rev.json", "w"))
PY2
rts compile --target zh --out "$ROUT/a" >/dev/null
rts compile --target zh --out "$ROUT/b" --content "$ROUT/rev.json" >/dev/null
# Compare every emitted type, ignoring only the pack-name prefix the two runs share anyway.
for f in Weapon Armor Locomotor Object CommandButton CommandSet PlayerTemplate; do
  fa=$(ls "$ROUT/a/Data/INI/$f/"*.ini 2>/dev/null | head -1)
  fb=$(ls "$ROUT/b/Data/INI/$f/"*.ini 2>/dev/null | head -1)
  [ -n "$fa" ] && [ -n "$fb" ] || continue
  # Strip comment lines: the banner carries contentHash, which SHOULD differ.
  if ! diff -q <(tr -d '\r' < "$fa" | grep -v '^;') <(tr -d '\r' < "$fb" | grep -v '^;') >/dev/null; then
    echo "  FILE ORDER LEAKED into $f: reordering the pack changed the emitted output"
    diff <(tr -d '\r' < "$fa" | grep -v '^;') <(tr -d '\r' < "$fb" | grep -v '^;') | head -6
    exit 1
  fi
done
echo "  reversing every keyed table in the pack emits byte-identical output"

gate "W3D round-trip and authoring"
# Adopting art needs a retail install and caps what a standalone conversion can be. The
# question is whether a model can be understood well enough to REMAKE, and the test is
# byte-identity: the high bit of a chunk's size marks a container, so sizes must be recomputed
# from children rather than remembered. Anything less than byte-identical means a re-model
# built on this parser would silently drop chunks the engine needs.
#
# Conditional on a local retail install: the samples are EA's art, extracted locally and never
# committed. Ship the extractor, never the extract.
W3DBIG="$HOME/GeneralsX/GeneralsZH/ZH_Generals/W3D.big"
if [ -f "$W3DBIG" ]; then
  WOUT=$(mktemp -d); trap 'rm -rf "$WOUT"' EXIT
  python3 - "$W3DBIG" "$WOUT" <<'PY2'
import struct, sys, os
big, out = sys.argv[1], sys.argv[2]
with open(big, "rb") as f:
    f.read(8); (n,) = struct.unpack(">I", f.read(4)); f.read(4)
    ents = []
    for _ in range(n):
        off, size = struct.unpack(">II", f.read(8)); nm = b""
        while True:
            c = f.read(1)
            if c in (b"\x00", b""): break
            nm += c
        ents.append((nm.decode("latin-1"), off, size))
    for nm, off, size in ents:
        base = nm.split("\\")[-1].upper()
        if base in ("ABPWRPLANT_D01.W3D", "AVLEOPARD.W3D", "AIHERO_SKL.W3D"):
            f.seek(off); open(os.path.join(out, base), "wb").write(f.read(size))
PY2
  for f in "$WOUT"/*.W3D; do
    ./tools/zhasset w3dround "$f" | grep -q "BYTE-IDENTICAL" \
      || { echo "  W3D round-trip is LOSSY for $(basename "$f") — a re-model would drop chunks"; exit 1; }
  done
  echo "  every sample model parses and re-emits byte-identically"

  # Authoring: geometry computed here, written into a template that supplies only material
  # state. A 24-vertex / 12-triangle template IS a box, so counts stay valid.
  ./tools/zhasset w3dbox --template "$WOUT/ABPWRPLANT_D01.W3D" --out "$WOUT/box.w3d" \
      --name E2EBOX --size 40 40 60 --out-dir "$WOUT" >/dev/null
  ./tools/zhasset w3d "$WOUT/box.w3d" | grep -q "24 vertices, 12 triangles" \
    || { echo "  authored mesh does not read back as 24 verts / 12 tris"; exit 1; }

  # ARBITRARY TOPOLOGY: counts that differ from the template's are what separate "a box" from
  # "any shape". MESH_HEADER3's NumVertices/NumTris and every per-vertex array have to be
  # rewritten together, and a mismatch between them renders as nothing at all.
  ./tools/zhasset w3dbox --template "$WOUT/ABPWRPLANT_D01.W3D" --out "$WOUT/cyl.w3d" \
      --name E2ECYL --shape cylinder --segments 16 --size 60 60 90 --out-dir "$WOUT" >/dev/null
  ./tools/zhasset w3d "$WOUT/cyl.w3d" | grep -q "98 vertices, 64 triangles" \
    || { echo "  cylinder did not read back at its authored counts"; exit 1; }
  ./tools/zhasset w3dround "$WOUT/cyl.w3d" | grep -q "BYTE-IDENTICAL" \
    || { echo "  an AUTHORED mesh does not round-trip — the writer emits something unreadable"; exit 1; }
  echo "  authored topology (98 verts / 64 tris from a 24/12 template) reads back and round-trips"
  # An authored mesh must DECLARE its contract, or the compiler falls back to guesses and a
  # building swallows what it builds.
  python3 -c "
import json,sys
p=json.load(open('$WOUT/art-profiles.json'))['models']['E2EBOX']
assert p['majorRadius']=='20.0' and p['height']=='60.0', p
" || { echo "  authored mesh did not declare its own art profile"; exit 1; }
  echo "  authored geometry reads back correct and declares its own profile"

  # A texture authored from nothing: 18-byte header then BGR triples. Verified by reading the
  # header back, because a plausible-looking file that the engine rejects is the usual failure.
  ./tools/zhasset tga --out "$WOUT/grid.tga" --size 64 --cells 4 >/dev/null
  python3 -c "
import struct, sys
d = open('$WOUT/grid.tga','rb').read()
_,_,dtc,_,_,_,_,_,w,h,bpp,_ = struct.unpack('<BBBHHBHHHHBB', d[:18])
assert dtc == 2 and bpp == 24 and w == h == 64, (dtc, bpp, w, h)
assert len(d) - 18 == w*h*3, (len(d)-18, w*h*3)
" || { echo "  authored TGA header does not describe its own payload"; exit 1; }

  # UV density must be UNIFORM across surfaces. Assigning 0..1 per surface gave a cylinder
  # side cells 3.1x wider than its cap cells -- visible in game, invisible to every other test
  # here. Assert the ratio directly from the emitted texcoords.
  python3 - "$WOUT/cyl.w3d" <<'PY5'
import struct, sys
buf = open(sys.argv[1], "rb").read()
def walk(off, end):
    while off + 8 <= end:
        t, raw = struct.unpack_from("<II", buf, off); sz = raw & 0x7FFFFFFF
        body = off + 8
        if raw & 0x80000000: yield from walk(body, body + sz)
        else: yield t, buf[body:body+sz]
        off = body + sz
verts = uvs = None
for t, p in walk(0, len(buf)):
    if t == 0x0002: verts = [struct.unpack_from("<3f", p, i*12) for i in range(len(p)//12)]
    if t == 0x004A: uvs = [struct.unpack_from("<2f", p, i*8) for i in range(len(p)//8)]
# Compare texel density on a side quad against a cap triangle: UV span per world span.
def density(i, j):
    du = ((uvs[i][0]-uvs[j][0])**2 + (uvs[i][1]-uvs[j][1])**2) ** 0.5
    dw = sum((verts[i][k]-verts[j][k])**2 for k in range(3)) ** 0.5
    return du/dw if dw > 1e-6 else None
side = density(0, 1)                       # first side quad, around the circumference
cap = next(d for d in (density(len(verts)-1, len(verts)-2),) if d)
ratio = max(side, cap) / min(side, cap)
if ratio > 1.15:
    print(f"  UV DENSITY UNEVEN: side {side:.4f} vs cap {cap:.4f} per world unit ({ratio:.2f}x)")
    sys.exit(1)
print(f"  texel density uniform across surfaces ({ratio:.2f}x side vs cap)")
PY5

  # Smooth shading is per-VERTEX normals varying across a face, not shared vertices. And the
  # TRIANGLES chunk must still carry the GEOMETRIC face normal — reusing a vertex normal there
  # was correct only while the two coincided under flat shading.
  python3 - "$WOUT/cyl.w3d" <<'PY6'
import struct, sys
buf = open(sys.argv[1], "rb").read()
def walk(off, end):
    while off + 8 <= end:
        t, raw = struct.unpack_from("<II", buf, off); sz = raw & 0x7FFFFFFF; body = off + 8
        if raw & 0x80000000: yield from walk(body, body + sz)
        else: yield t, buf[body:body+sz]
        off = body + sz
V = N = T = None
for t, p in walk(0, len(buf)):
    if t == 0x0002: V = [struct.unpack_from("<3f", p, i*12) for i in range(len(p)//12)]
    if t == 0x0003: N = [struct.unpack_from("<3f", p, i*12) for i in range(len(p)//12)]
    if t == 0x0020: T = [struct.unpack_from("<3II3ff", p, i*32) for i in range(len(p)//32)]

if not any(abs(N[0][k] - N[1][k]) > 1e-4 for k in range(3)):
    print("  NOT SMOOTH: a curved surface has identical normals across a face"); sys.exit(1)
# Every wall normal must be unit-length and radial (z=0) for a cylinder.
for i in (0, 1, 2, 3):
    ln = sum(c*c for c in N[i]) ** 0.5
    if abs(ln - 1.0) > 1e-3 or abs(N[i][2]) > 1e-3:
        print(f"  wall normal {i} is not a unit radial vector: {N[i]} len {ln:.4f}"); sys.exit(1)
i0, i1, i2 = T[0][0], T[0][1], T[0][2]
fn = (T[0][4], T[0][5], T[0][6])
p0, p1, p2 = V[i0], V[i1], V[i2]
u = [p1[k]-p0[k] for k in range(3)]; v = [p2[k]-p0[k] for k in range(3)]
c = [u[1]*v[2]-u[2]*v[1], u[2]*v[0]-u[0]*v[2], u[0]*v[1]-u[1]*v[0]]
ln = sum(x*x for x in c) ** 0.5
c = [x/ln for x in c]
if any(abs(fn[k] - c[k]) > 1e-3 for k in range(3)):
    print(f"  TRIANGLES normal is not geometric: stored {fn}, computed {tuple(c)}"); sys.exit(1)
if not any(abs(fn[k] - N[i0][k]) > 1e-3 for k in range(3)):
    print("  face normal equals a vertex normal — smooth shading cannot be in effect"); sys.exit(1)
print("  smooth: per-vertex radial normals, and TRIANGLES keeps the geometric face normal")
PY6

  # SYSTEMATIC SWEEP. Four bugs running were the same shape: a field whose absence is SILENT
  # and whose default is wrong for our case — InitialHealth defaults to 0 rather than
  # MaxHealth, the art-side Turret defaults to no bone, ProductionUpdate and PhysicsBehavior
  # were simply absent. Every one was found by a human playing the game. This finds them by
  # diffing each emitted object against the retail objects sharing its mesh, at build time.
  rts compile --target zh --out "$WOUT/objs" --mod content/mods/demo-attach.json >/dev/null
  if ! ./tools/zhasset objectdiff --emitted "$WOUT/objs/Data/INI/Object" \
       | grep -q "no behaviour gaps in any emitted object"; then
    echo "  EMITTED OBJECTS HAVE BEHAVIOUR GAPS versus their retail peers:"
    ./tools/zhasset objectdiff --emitted "$WOUT/objs/Data/INI/Object" \
      | grep -E "^demo|BEHAVIOUR gaps" | head -8
    exit 1
  fi
  echo "  no behaviour gaps against retail peers sharing the same mesh"

  # SKINNING + ANIMATION. Geometry bound to a skeleton, and motion, both authored from nothing.
  # Every size here is load-bearing: the loader derives element counts by DIVIDING chunk size by
  # struct size, so a wrong struct size yields the wrong number of bones, vertices or frames
  # rather than an error.
  ./tools/zhasset w3dskel --out "$WOUT/sk.w3d" --name E2E_SKL --bones 3 --segment 70 >/dev/null
  ./tools/zhasset w3dbox --template "$WOUT/ABPWRPLANT_D01.W3D" --out "$WOUT/skin.w3d" \
      --name E2ESKIN --shape cylinder --segments 16 --size 40 40 140 \
      --skin E2E_SKL --skin-split 70 --out-dir "$WOUT" >/dev/null
  ./tools/zhasset w3danim --out "$WOUT/anim.w3d" --name E2EANIM --skeleton E2E_SKL \
      --frames 30 --pivot 2 --axis 1 0 0 >/dev/null
  for f in sk skin anim; do
    ./tools/zhasset w3dround "$WOUT/$f.w3d" | grep -q "BYTE-IDENTICAL" \
      || { echo "  authored $f.w3d does not round-trip"; exit 1; }
  done
  python3 - "$WOUT" <<'PY7'
import struct, sys
d = sys.argv[1]
def chunks(buf, off=0, end=None):
    end = len(buf) if end is None else end
    while off + 8 <= end:
        t, raw = struct.unpack_from("<II", buf, off); sz = raw & 0x7FFFFFFF; body = off + 8
        if raw & 0x80000000: yield from chunks(buf, body, body + sz)
        else: yield t, buf[body:body+sz]
        off = body + sz

sk = dict((t, p) for t, p in chunks(open(f"{d}/sk.w3d","rb").read()))
hh, pv = sk[0x0101], sk[0x0102]
if struct.unpack_from("<I", hh, 20)[0] != 3 or len(pv) != 3 * 60:
    print(f"  skeleton wrong: NumPivots={struct.unpack_from('<I',hh,20)[0]} pivots={len(pv)}B"); sys.exit(1)
if struct.unpack_from("<I", pv, 16)[0] != 0xFFFFFFFF:
    print("  pivot 0 must be the parentless root (0xFFFFFFFF)"); sys.exit(1)

buf = open(f"{d}/skin.w3d","rb").read()
c = {}
for t, p in chunks(buf): c.setdefault(t, p)
attrs = struct.unpack_from("<I", c[0x001F], 4)[0]
nv = struct.unpack_from("<I", c[0x001F], 44)[0]
if attrs & 0x00FF0000 != 0x00020000:
    print(f"  mesh is not marked SKIN: geomType 0x{attrs & 0xFF0000:06X}"); sys.exit(1)
if len(c[0x000E]) != nv * 8:
    print(f"  VERTEX_INFLUENCES {len(c[0x000E])}B != NumVertices*8 ({nv*8})"); sys.exit(1)
if c[0x001F][24:40].rstrip(b"\0") != b"E2ESKIN":
    print("  ContainerName must match what the HLOD sub-object names"); sys.exit(1)
if c[0x0701][24:40].rstrip(b"\0") != b"E2E_SKL":
    print("  HLOD does not bind the skeleton by name — a skin without it cannot deform"); sys.exit(1)
bones = {struct.unpack_from("<H", c[0x000E], i*8)[0] for i in range(nv)}
if bones != {1, 2}:
    print(f"  expected vertices split across bones 1 and 2, got {sorted(bones)}"); sys.exit(1)

an = dict((t, p) for t, p in chunks(open(f"{d}/anim.w3d","rb").read()))
ah = an[0x0201]
if ah[4:20].rstrip(b"\0") != b"E2EANIM" or ah[20:36].rstrip(b"\0") != b"E2E_SKL":
    print("  animation header names wrong — registered name is Hierarchy + '.' + Name"); sys.exit(1)
frames = struct.unpack_from("<I", ah, 36)[0]
ch = an[0x0202]
first, last, vlen, flags, pivot, _ = struct.unpack_from("<6H", ch, 0)
if vlen != 4 or flags != 6:
    print(f"  expected a quaternion channel (VectorLen 4, Flags 6), got {vlen}/{flags}"); sys.exit(1)
if len(ch) - 12 != frames * 4 * 4:
    print(f"  channel payload {len(ch)-12}B != frames*VectorLen*4 ({frames*16})"); sys.exit(1)
print(f"  skeleton 3 pivots, skin {nv} verts across bones {sorted(bones)}, "
      f"anim E2E_SKL.E2EANIM {frames} frames")
PY7
else
  echo "  (skipped: no local retail install at $W3DBIG)"
fi

gate "MCP server — the agent-facing seam"
# The whole project points at this: an agent authors a pack, is told exactly what is wrong,
# measures it, and compiles it, with no human in the loop. JSON-RPC 2.0 over newline-delimited
# stdio and ZERO dependencies, so the root NuGet.config's <clear/> stays untouched.
MOUT=$(mktemp -d); trap 'rm -rf "$DOUT" "$UOUT" "$FOUT" "$GOUT" "$SOUT" "$OVO" "$POUT" "$MOUT"' EXIT
python3 - > "$MOUT/in.jsonl" <<'PY2'
import json
mod = {"meta": {"name": "agent-test", "version": 1},
       "units": {"glass_tank": {"faction": "usa", "cost": 700, "buildSeconds": 12,
           "kindOf": ["VEHICLE", "SELECTABLE", "CAN_ATTACK"],
           "components": {"Health": {"max": 200, "armorClass": "light_vehicle"},
                          "Mobile": {"speed": 7.0},
                          "WeaponBearer": {"weapon": "crusader_cannon"}}}},
       "zh": {"models": {"glass_tank": "AVLeopard"}}}
for m in [
  {"jsonrpc":"2.0","id":1,"method":"initialize","params":{}},
  {"jsonrpc":"2.0","method":"notifications/initialized"},
  {"jsonrpc":"2.0","id":2,"method":"tools/list"},
  {"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"put_pack","arguments":{"content":json.dumps(mod)}}},
  {"jsonrpc":"2.0","id":4,"method":"tools/call","params":{"name":"run_matchup","arguments":{"pack":"PUT_HASH","a":"glass_tank","b":"crusader","n":8,"seed":42}}},
  {"jsonrpc":"2.0","id":5,"method":"tools/call","params":{"name":"run_matchup","arguments":{"pack":"PUT_HASH","a":"glass_tank","b":"nope"}}},
]: print(json.dumps(m))
PY2
# put_pack returns the hash the later calls need, so run the store step first and substitute.
h=$(head -4 "$MOUT/in.jsonl" | rts mcp --store "$MOUT/store" 2>/dev/null \
    | python3 -c "
import sys, json
for l in sys.stdin:
    d = json.loads(l)
    if d.get('id') == 3: print(json.loads(d['result']['content'][0]['text'])['contentHash'])")
[ -n "$h" ] || { echo "  put_pack returned no contentHash"; exit 1; }
[ -f "$MOUT/store/$h.json" ] || { echo "  put_pack did not store the pack under its hash"; exit 1; }
echo "  put_pack stores by contentHash ($h) — the loop is stateless for the agent"

sed "s/PUT_HASH/$h/" "$MOUT/in.jsonl" | rts mcp --store "$MOUT/store" 2>/dev/null > "$MOUT/out.jsonl"
python3 - "$MOUT/out.jsonl" <<'PY3'
import json, sys
res = {}
for l in open(sys.argv[1]):
    d = json.loads(l)
    if d.get("id") is not None: res[d["id"]] = d

if res[1]["result"]["protocolVersion"] != "2024-11-05":
    print("  wrong protocolVersion in initialize"); sys.exit(1)
names = [t["name"] for t in res[2]["result"]["tools"]]
for want in ("put_pack","validate_mod","run_matchup","query_counter_matrix","compare_packs"):
    if want not in names:
        print(f"  tools/list is missing the roadmap tool '{want}'"); sys.exit(1)
for t in res[2]["result"]["tools"]:
    if "inputSchema" not in t or "description" not in t:
        print(f"  tool '{t['name']}' has no schema or description — an agent cannot call it"); sys.exit(1)

m = json.loads(res[4]["result"]["content"][0]["text"])
# contentHash on EVERY result: a balance number without it is not reproducible.
if "contentHash" not in m or "lastFinalHash" not in m:
    print("  run_matchup result carries no provenance"); sys.exit(1)
if m["runs"] != 8:
    print(f"  run_matchup ignored n: got {m['runs']}"); sys.exit(1)

# A bad prototype must come back as a REPAIR INSTRUCTION, not a raw exception.
err = res[5].get("error", {}).get("message", "")
if "no prototype 'nope'" not in err or "Available:" not in err:
    print(f"  bad input did not produce an actionable error: {err[:80]}"); sys.exit(1)
print(f"  handshake, {len(names)} tools with schemas, results carry contentHash, errors name the fix")
PY3

gate "an authored map crosses to a real .map"
# The pivot terrain has been waiting for: until now every terrain number the harness produced
# was measured on a map their engine had never seen, and target-zh lint said so as a
# divergence. `rts compile` now writes an actual .map, so the SAME authored shape can be
# checked against THEIR rules.
#
# The reader doing the checking is a second, independent implementation (`zhasset map`), and
# it earns that job by decoding the whole shipped corpus first. A writer graded by its own
# reader proves only that the two agree.
MAPD=$(mktemp -d); trap 'rm -rf "$MAPD"' EXIT

CORPUS="$HOME/GeneralsX/GeneralsZH"
if [ -d "$CORPUS" ]; then
  for arc in "$CORPUS/MapsZH.big" "$CORPUS/ZH_Generals/maps.big"; do
    [ -f "$arc" ] && ./tools/zhasset archives extract --archive "$arc" --glob '*.map' \
      --dest "$MAPD/corpus" >/dev/null 2>&1
  done
  scan=$(./tools/zhasset map scan "$MAPD/corpus" 2>&1 || true)
  total=$(echo "$scan" | sed -n 's/^ *\([0-9]*\)\/\([0-9]*\) maps decoded.*/\1 \2/p')
  case "$total" in
    "") echo "  MAP CORPUS SCAN PRODUCED NO RESULT"; echo "$scan"; exit 1 ;;
    *) ok=${total%% *}; all=${total##* }
       [ "$ok" = "$all" ] || { echo "  READER FAILED ON $((all-ok)) OF $all SHIPPED MAPS"
                               echo "$scan"; exit 1; }
       echo "  reader decodes $ok/$all shipped maps — all 8 chunks, every version" ;;
  esac
else
  echo "  (no retail install: reader-vs-corpus check skipped)"
fi

# Two authored shapes that our own sim already disagrees about: gate 18 measures the gated
# wall at 27.4s and the solid one as a timeout draw. Their engine must reach the same verdict
# from a height field alone — no passability layer, no flags, just slope.
python3 - "$MAPD" <<'PYMAP'
import json, re, sys
d = json.loads(re.sub(r'^\s*//.*$', '', open('content/mods/chokepoint.json').read(), flags=re.M))
rows, legend = d['map']['rows'], d['map']['legend']
wall = next(k for k, v in legend.items() if v != 'clear')
d['meta']['name'] = 'e2egate'
open(f'{sys.argv[1]}/gated.json', 'w').write(json.dumps(d))
d = json.loads(json.dumps(d))
d['meta']['name'] = 'e2esolid'
d['map']['rows'] = [''.join(wall if 21 <= i <= 26 else c for i, c in enumerate(r)) for r in rows]
open(f'{sys.argv[1]}/solid.json', 'w').write(json.dumps(d))
PYMAP

rts compile --target zh --out "$MAPD/gated" --mod "$MAPD/gated.json" >/dev/null
rts compile --target zh --out "$MAPD/solid" --mod "$MAPD/solid.json" >/dev/null

gated_map="$MAPD/gated/Maps/e2egate_map/e2egate_map.map"
solid_map="$MAPD/solid/Maps/e2esolid_map/e2esolid_map.map"
[ -f "$gated_map" ] || { echo "  NO .map EMITTED for a pack that authored one"; exit 1; }

# A pack with NO map must emit no Maps/ directory at all: opt-in by content, the same rule
# every slice since structures has followed.
rts compile --target zh --out "$MAPD/nomap" >/dev/null
[ ! -d "$MAPD/nomap/Maps" ] || { echo "  A MAPLESS PACK EMITTED A MAP"; exit 1; }
echo "  opt-in by content: no authored map, no Maps/ directory"

./tools/zhasset map verify "$gated_map" --expect-cliff --expect-connected \
  || { echo "  GATED MAP FAILED VERIFICATION"; exit 1; }
./tools/zhasset map verify "$solid_map" --expect-cliff --expect-separated \
  || { echo "  SOLID BARRIER LEAKS IN THEIR ENGINE"; exit 1; }
echo "  the same two shapes separate and connect under THEIR cliff rule, not ours"

# The map must name a TerrainType that retail actually declares. An absent one is the
# quietest failure in the format: readTexClass simply fails to open the file and returns,
# and the map draws untextured with no error anywhere.
tt=$(./tools/zhasset map read "$gated_map" | sed -n "s/.*terrain classes: \['\([^']*\)'\].*/\1/p")
TERRAIN_INI="$HOME/work/oss/zh-retail/Data/INI/Terrain.ini"
if [ -f "$TERRAIN_INI" ]; then
  # Two traps in one line, both of which this file has hit before:
  #   tr -d '\r' — retail INI is CRLF, so a "$" anchor matches nothing (gate 18).
  #   grep -c, not -q — under `set -o pipefail`, grep -q exits on the first match, SIGPIPEs
  #   the upstream tr, and the pipeline reports failure on a SUCCESSFUL match.
  hit=$(tr -d '\r' < "$TERRAIN_INI" | grep -ci "^Terrain $tt\$" || true)
  [ "$hit" -ge 1 ] \
    || { echo "  INVENTED TERRAIN TYPE '$tt': retail declares no such block"; exit 1; }
  echo "  terrain type '$tt' is one retail declares, not one that sounded plausible"
fi

# map.ini is only read from beside a .map — GameLogic::loadMapINI is the sole caller using
# INI_LOAD_CREATE_OVERRIDES and it runs against the chosen map's own directory. Emitting it
# to the pack root left it somewhere the engine never looks.
rts compile --target zh --out "$MAPD/ovr" --mod content/mods/chokepoint.json \
  --mod content/mods/retail-override.json >/dev/null
if [ -f "$MAPD/ovr/map.ini" ]; then
  echo "  OVERRIDES LEFT AT THE PACK ROOT, where loadMapINI never looks"; exit 1
fi
# The stack is named for its TOP layer, so the map directory is the override pack's, not the
# terrain pack's — find it rather than spelling it.
beside=$(find "$MAPD/ovr/Maps" -name map.ini | head -1)
[ -n "$beside" ] || { echo "  OVERRIDES NOT PLACED BESIDE THE EMITTED MAP"; exit 1; }
[ -f "$(dirname "$beside")"/*.map ] 2>/dev/null || \
  ls "$(dirname "$beside")"/*.map >/dev/null 2>&1 \
  || { echo "  map.ini has no .map beside it, so loadMapINI will never read it"; exit 1; }
echo "  overrides land beside the .map, which is the only place they are read"
rm -rf "$MAPD"; trap - EXIT

echo
echo "== regression: pinned replay hashes =="
# Re-pinned twice, both deliberate, both recorded:
#   1. prototype identity moved from the dense table index to a name-derived StableId
#      (docs/ZERO-HOUR-ANATOMY.md §8). Previous pins: df8b977f31362dda / 4a8a45c49b0fffce.
#   2. UnitState gained ClipRemaining (burst-fire weapons). Every sim-state field must
#      enter the hash in a fixed position, so ADDING one necessarily moves every hash —
#      that is the invariant working, not a regression. Previous pins:
#      facc617cce5e8ce6 / 2e0ed7322422be60 / 736b5a6718e4a434.
# Pins from different eras are not comparable and must never be "restored".
#
# NOTE these pins alone are a weak guard: they only observe units ALIVE in the final
# state, so they are blind to renumbering of anything that dies. That is exactly how a
# real renumbering bug survived here undetected. The generative check below is the
# guard that actually holds; these pins just catch unintended sim changes.
for pair in "7a3e280081f484c6:duel --a crusader --b technical --n 1 --seed 42 --json" \
            "cc503fc148421b98:duel --a technical --b ranger --n 1 --seed 42 --json" \
            "c1fcdb0cd48530d8:econ --a war_factory,battlemaster* --b war_factory,crusader* --n 1 --seed 42 --json"; do
  want=${pair%%:*}; cmd=${pair#*:}
  got=$(rts $cmd | sed -n 's/.*"lastFinalHash": "\(.*\)".*/\1/p')
  if [ "$got" != "$want" ]; then echo "  REPLAY DRIFT: got $got want $want ($cmd)"; exit 1; fi
  echo "  stable: $got"
done

echo
echo "== regression: content growth must not renumber an existing prototype =="
# Generative, not pinned. Inject a unit that never spawns, joins no roster and is
# referenced by nothing, under names that sort BEFORE and AFTER every existing unit.
# Under ordinal identity the "aaa_" variant shifts every later prototype and rewrites
# replays; under name-derived identity nothing moves. Every matchup must agree across
# all three packs — including ones where the shifted unit SURVIVES, which is the case
# the pinned hashes above cannot see.
TMP=$(mktemp -d); trap 'rm -rf "$TMP"' EXIT
python3 - "$TMP" <<'PY'
import sys
tmp = sys.argv[1]
src = open('content/game.json').read()
block = '''    "%s": {
      "faction": "usa", "cost": 300, "buildTicks": 150, "prerequisites": [],
      "components": {
        "Health": { "max": 120, "armorClass": "infantry" },
        "Mobile": { "speed": 5.0 },
        "WeaponBearer": { "weapon": "ranger_rifle" },
        "VeterancyCarrier": { "track": "infantry_default" }
      }
    },
'''
for name in ("aaa_dummy", "zzz_dummy"):
    open(f"{tmp}/{name}.json", "w").write(
        src.replace('  "units": {\n', '  "units": {\n' + block % name, 1))
PY

fail=0
for m in "crusader technical" "technical ranger" "battlemaster ranger" "ranger technical"; do
  set -- $m
  base=$(rts duel --a "$1" --b "$2" --n 1 --seed 42 --json | sed -n 's/.*"lastFinalHash": "\(.*\)".*/\1/p')
  for v in aaa_dummy zzz_dummy; do
    got=$(rts duel --a "$1" --b "$2" --n 1 --seed 42 --json --content "$TMP/$v.json" \
          | sed -n 's/.*"lastFinalHash": "\(.*\)".*/\1/p')
    if [ "$got" != "$base" ]; then
      echo "  PROTOTYPE RENUMBERING: $1 vs $2 with $v -> $got, base $base"; fail=1
    fi
  done
  echo "  $1 vs $2: identity-stable ($base)"
done
[ "$fail" -eq 0 ] || exit 1

# --- presentation shell (optional; skipped when Godot isn't installed) ---------
GODOT="${GODOT:-$HOME/Applications/Godot_mono.app/Contents/MacOS/Godot}"
if [ -x "$GODOT" ]; then
  echo
  echo "== shell: build =="
  (cd godot && dotnet build --nologo -v quiet)
  echo "shell build: OK"

  echo
  echo "== shell: sim equivalence =="
  # The shell must reach the same final state hash as the harness from the same
  # (contentHash, seed, command log) — proof that presentation cannot perturb
  # the sim. Compared against `rts <verb> --json` for the same scenarios.
  shell_out=$("$GODOT" --headless --path godot -- --verify 2>/dev/null | grep '^scenario=')
  echo "$shell_out"

  duel_hash=$(rts duel --a crusader --b technical --n 1 --seed 42 --json | sed -n 's/.*"lastFinalHash": "\(.*\)".*/\1/p')
  econ_hash=$(rts econ --a "war_factory,battlemaster*" --b "war_factory,crusader*" --n 1 --seed 42 --json | sed -n 's/.*"lastFinalHash": "\(.*\)".*/\1/p')

  for pair in "SetPieceDuel:$duel_hash" "MacroBattle:$econ_hash"; do
    name=${pair%%:*}; want=${pair#*:}
    got=$(echo "$shell_out" | sed -n "s/.*scenario=$name .*finalHash=\([0-9a-f]*\).*/\1/p")
    if [ "$got" != "$want" ]; then
      echo "  MISMATCH $name: shell=$got harness=$want"
      exit 1
    fi
    echo "  $name: shell == harness ($got)"
  done
else
  echo
  echo "== shell: skipped (no Godot at $GODOT; set GODOT=/path/to/Godot) =="
fi

echo
echo "E2E PASS"

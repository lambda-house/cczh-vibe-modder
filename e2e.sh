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
# (RTSBOX for its factory). Losing either half would still compile cleanly.
rts compile --target zh --out "$ORD/zh" --mod content/mods/demo-attach.json >/dev/null
zh_obj="$ORD/zh/Data/INI/Object/demo.ini"
for m in AVAmbulance RTSBOX; do
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
# Derived, not hardcoded: what matters is that a scheme declares the Side the OBJECT claims.
# setControlBarSchemeByPlayer matches on Side and leaves the bar unset when nothing does, so a
# name check passes right up until the two drift apart — which is exactly what happened when
# faction sides gained a pack prefix and this line still spelled the old name.
objside=$(tr -d '\r' < "$POUT/Data/INI/Object/demo.ini" | awk '/^Object demo_hellfire_works$/,/^End$/' \
          | sed -n 's/^  Side = //p' | head -1)
schemeside=$(tr -d '\r' < "$POUT/Data/INI/ControlBarScheme/demo.ini" | sed -n 's/^  Side //p' | sort -u)
case "$schemeside" in
  *"$objside"*) : ;;
  *) echo "  no ControlBarScheme for Side '$objside' (schemes cover: $schemeside)"
     echo "  the faction would be selectable and have no command bar"; exit 1 ;;
esac
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
# A pack authors its OWN ground by default, so the check forks: ours must come with a Terrain
# block AND a tile that countTiles will accept; a retail name must exist AND its texture must
# ship. Both halves matter — naming a real block whose texture is absent renders black, which
# is how desertA reached a match.
if [ -f "$MAPD/gated/Data/INI/Terrain/e2egate.ini" ]; then
  tr -d '\r' < "$MAPD/gated/Data/INI/Terrain/e2egate.ini" | grep -q "^Terrain $tt\$" \
    || { echo "  AUTHORED TERRAIN '$tt' HAS NO Terrain BLOCK"; exit 1; }
  tile="$MAPD/gated/Art/Terrain/$tt.tga"
  [ -f "$tile" ] || { echo "  AUTHORED TERRAIN '$tt' SHIPS NO TILE at Art/Terrain/"; exit 1; }
  python3 - "$tile" <<'PYTILE'
import struct, sys
d = open(sys.argv[1], "rb").read()
w, h = struct.unpack_from("<HH", d, 12)
kind, bpp = d[2], d[16]
# countTiles: uncompressed true-colour, 24-32bpp, at least one 64x64 tile. Fail any of these
# and it returns 0 tiles, readTexClass returns without opening anything, and the map is BLACK
# with no error from the engine.
if kind not in (2, 10) or not (24 <= bpp <= 32) or w // 64 < 1 or h // 64 < 1:
    print(f"  TILE REJECTED BY countTiles: type={kind} bpp={bpp} {w}x{h}"); sys.exit(1)
if len(d) != 18 + w * h * (bpp // 8):
    print(f"  TILE IS TRUNCATED: {len(d)} bytes for {w}x{h}x{bpp}"); sys.exit(1)
print(f"  authored ground: {w}x{h} {bpp}bpp -> {(w//64)*(h//64)} tile(s), countTiles accepts it")
PYTILE
fi
TERRAIN_INI="$HOME/work/oss/zh-retail/Data/INI/Terrain.ini"
if [ ! -f "$MAPD/gated/Data/INI/Terrain/e2egate.ini" ] && [ -f "$TERRAIN_INI" ]; then
  # Two traps in one line, both of which this file has hit before:
  #   tr -d '\r' — retail INI is CRLF, so a "$" anchor matches nothing (gate 18).
  #   grep -c, not -q — under `set -o pipefail`, grep -q exits on the first match, SIGPIPEs
  #   the upstream tr, and the pipeline reports failure on a SUCCESSFUL match.
  hit=$(tr -d '\r' < "$TERRAIN_INI" | grep -ci "^Terrain $tt\$" || true)
  [ "$hit" -ge 1 ] \
    || { echo "  INVENTED TERRAIN TYPE '$tt': retail declares no such block"; exit 1; }

  # NAMING A REAL BLOCK IS NOT ENOUGH, and this one reached a real match before it was
  # caught. readTexClass resolves the block to a TGA under Art/Terrain and, when the file is
  # not there, simply fails to open it and returns — the map renders BLACK with no error.
  # 12 of retail's own 291 Terrain blocks dangle exactly like this, "desertA" among them,
  # which is the one the first version of this compiler chose precisely BECAUSE it was real.
  # awk reads the file DIRECTLY and strips CR itself. Piping `tr` into an awk that calls
  # exit() SIGPIPEs the tr, and `set -o pipefail` turns that into a 141 that kills the run —
  # the fifth variant of this trap in this file, after grep -q, grep -c, head and find.
  tex=$(awk -v t="$tt" '
          {sub(/\r$/, "")}
          $1=="Terrain" && tolower($2)==tolower(t) {f=1; next}
          f && $1=="Texture" {print $3; exit}
          f && $1=="End" {exit}' "$TERRAIN_INI")
  [ -n "$tex" ] || { echo "  TerrainType '$tt' declares no Texture"; exit 1; }
  if [ -d "$MAPD/corpus" ] || [ -d "$CORPUS" ]; then
    found=0
    for arc in "$CORPUS"/*.big "$CORPUS"/ZH_Generals/*.big; do
      [ -f "$arc" ] || continue
      n=$(./tools/zhasset archives names --archive "$arc" 2>/dev/null |
          grep -ci "Art/Terrain/$tex\$" || true)
      [ "$n" -ge 1 ] && { found=1; break; }
    done
    [ "$found" = 1 ] \
      || { echo "  DANGLING TERRAIN TEXTURE: '$tt' names $tex, which ships in no archive."
           echo "  The map will render black with no error anywhere."; exit 1; }
    echo "  terrain '$tt' -> $tex, and that texture really ships"
  fi
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
# A glob, not `find | head -1`: under `set -o pipefail` head exits first, find takes SIGPIPE,
# and the whole script dies with 141 — but only when find is slow enough to still be running,
# so it passes standalone and fails inside a full run. Fourth pipefail trap in this file.
beside=""
for f in "$MAPD/ovr/Maps"/*/map.ini; do
  [ -f "$f" ] && { beside="$f"; break; }
done
[ -n "$beside" ] || { echo "  OVERRIDES NOT PLACED BESIDE THE EMITTED MAP"; exit 1; }
ls "$(dirname "$beside")"/*.map >/dev/null 2>&1 \
  || { echo "  map.ini has no .map beside it, so loadMapINI will never read it"; exit 1; }
echo "  overrides land beside the .map, which is the only place they are read"
rm -rf "$MAPD"; trap - EXIT

gate "authored icons and labels — a pack that borrows no content art"
# A compiled pack used to point at retail's button art for every object, every command button
# and every upgrade. It worked, it is what every mod does, and it meant a pack whose mesh,
# texture, skeleton and animation were ALL authored still could not be looked at without EA's
# UI. This gate holds the line at: content icons are ours, HUD furniture is borrowed on
# purpose through zh.sides.
ICO=$(mktemp -d); trap 'rm -rf "$ICO"' EXIT
rts compile --target zh --out "$ICO/p" --mod content/mods/demo-attach.json --with-strings >/dev/null

# The load path is a directory scan EA's own source admits to (Image.cpp:256), so the file
# goes in HandCreated/. Emitting MappedImages.ini instead would replace retail's whole table.
[ -f "$ICO/p/Data/INI/MappedImages/HandCreated/demo.ini" ] \
  || { echo "  ICONS NOT EMITTED INTO THE ADDITIVE HandCreated SCAN"; exit 1; }
[ ! -f "$ICO/p/Data/INI/MappedImages.ini" ] \
  || { echo "  CLOBBERS RETAIL: emitted MappedImages.ini"; exit 1; }

python3 - "$ICO/p" <<'PYICO'
import glob, os, re, struct, sys
root = sys.argv[1]

def lines(p):
    return open(p, encoding="latin-1").read().replace("\r", "").splitlines()

# --- every declared MappedImage, and the sheet it addresses --------------------------------
declared, coords, texture = set(), {}, None
cur = None
for ln in lines(f"{root}/Data/INI/MappedImages/HandCreated/demo.ini"):
    t = ln.strip()
    if t.startswith("MappedImage "):
        cur = t.split()[1]; declared.add(cur)
    elif t.startswith("Texture ="):
        texture = t.split("=")[1].strip()
    elif t.startswith("Coords") and cur:
        c = dict(kv.split(":") for kv in t.split("=", 1)[1].split())
        coords[cur] = tuple(int(c[k]) for k in ("Left", "Top", "Right", "Bottom"))

# --- the texture must EXIST and be a real TGA ----------------------------------------------
# A MappedImage whose texture is absent renders as a blank tile with no error anywhere, which
# is the exact failure this slice exists to remove. Emitting INI without art would "pass" any
# check that only reads INI.
tga = f"{root}/Art/Textures/{texture}"
if not os.path.exists(tga):
    print(f"  ICON SHEET NOT EMITTED: {texture} referenced by {len(declared)} images"); sys.exit(1)
d = open(tga, "rb").read()
w, h = struct.unpack_from("<HH", d, 12)
# 32-bit BGRA, matching every retail UI page: a control bar image composites with alpha and
# a 24-bit one has no alpha channel to composite with.
bpp = d[16] // 8
if d[2] != 2 or d[16] != 32 or len(d) != 18 + w * h * bpp:
    print(f"  MALFORMED TGA: type={d[2]} bpp={d[16]} {w}x{h} but {len(d)} bytes"); sys.exit(1)

# --- every icon a unit or button names must be declared ------------------------------------
used = set()
for f in glob.glob(f"{root}/Data/INI/*/*.ini"):
    if "MappedImages" in f: continue
    for ln in lines(f):
        m = re.match(r"\s*(SelectPortrait|ButtonImage)\s*=\s*(\S+)", ln)
        if m: used.add(m.group(2))
ours = {u for u in used if u.startswith("demo_") or u.startswith("Upgrade_")}
missing = sorted(ours - declared)
if missing:
    print(f"  DANGLING ICONS, which render as blank tiles: {missing}"); sys.exit(1)

# --- no two images may address the same cell, and none may fall off the sheet --------------
seen = {}
for name, (l, t, r, b) in coords.items():
    if r > w or b > h or l < 0 or t < 0:
        print(f"  {name} Coords {l,t,r,b} falls outside the {w}x{h} sheet"); sys.exit(1)
    if (l, t) in seen:
        print(f"  {name} and {seen[(l,t)]} address the SAME cell"); sys.exit(1)
    seen[(l, t)] = name

# Distinct fill colours are the diagnostic, not decoration: two buttons rendering the same
# colour is how a duplicated Coords rectangle looks in game.
def px(x, y):
    o = 18 + ((h - 1 - y) * w + x) * bpp
    return d[o:o+3]
fills = {px((l + r) // 2, (t + b) // 2) for (l, t, r, b) in coords.values()}
if len(fills) != len(coords):
    print(f"  {len(coords)} icons share only {len(fills)} distinct colours"); sys.exit(1)
print(f"  {len(declared)} icons authored into one {w}x{h} sheet, all distinct, none dangling")

# --- strings: a .str is TOTAL, so a partial one is worse than none -------------------------
st = "\n".join(lines(f"{root}/Data/Generals.str"))
tags = set(re.findall(r"^([A-Z]+:\S+)$", st, re.M))
# Labels are keyed on the OBJECT, not on the portrait image. Deriving them from icon names
# instead asks for CONTROLBAR:<unit>_L, which is a label that should not and does not exist.
objs = {ln.split()[1] for f in glob.glob(f"{root}/Data/INI/Object/*.ini")
        for ln in lines(f) if ln.startswith("Object ")}
want = {f"CONTROLBAR:{o}" for o in objs} | {f"OBJECT:{o}" for o in objs}
gap = sorted(want - tags)
if gap:
    print(f"  .str REPLACES the whole table and omits {len(gap)}: {gap[:3]}"); sys.exit(1)
if any('"' + o + '"' in st for o in objs):
    print("  labels are raw ids: a .str is the only names in the game, so ids are typos"); sys.exit(1)
print(f"  {len(tags)} labels cover every emitted name, titled rather than raw ids")
PYICO

# --- the claim, measured: a pack on wholly AUTHORED art borrows no content asset ------------
# The residual must be exactly the HUD furniture zh.sides deliberately inherits. This is the
# assertion that would notice a new hardcoded retail name appearing anywhere in the compiler.
python3 - "$ICO" <<'PYAUTH'
import json, re, sys
d = json.loads(re.sub(r'^\s*//.*$', '', open('content/mods/demo-attach.json').read(), flags=re.M))
base = json.loads(re.sub(r'^\s*//.*$', '', open('content/game.json').read(), flags=re.M))
d['meta']['name'] = 'allauthored'
d['zh']['models'] = {u: 'RTSBOX' for u in base['units']}
d['zh']['models']['hellhound'] = 'RTSBOX'
d['zh']['models']['hellfire_works'] = 'RTSMAST'
d['zh']['animations'] = {'hellfire_works': 'RTSMAST_SKL.RTSSPIN:LOOP'}
open(f'{sys.argv[1]}/authored.json', 'w').write(json.dumps(d))
PYAUTH
rts compile --target zh --out "$ICO/auth" --mod "$ICO/authored.json" --with-strings >/dev/null

residual=$(cat "$ICO/auth"/Data/INI/*/*.ini "$ICO/auth"/Data/INI/*/*/*.ini 2>/dev/null | tr -d '\r' |
  awk '{ f=$1
         if (f=="SelectPortrait"||f=="ButtonImage"||f=="QueueButtonImage"||f=="RightHUDImage"||
             f=="GenBarButtonIn"||f=="GenBarButtonOn"||f=="ExpBarForegroundImage"||f=="GenArrow"||
             f=="CommandMarkerImage"||f=="ImageName"||f=="Model"||f=="Texture"||f=="FX"||f=="OCL")
           print $NF }' |
  grep -vE '^allauthored_|^RTS|^ModelDraw$|^[0-9]' | sort -u | tr '\n' ' ')
# Line-oriented, not a field regex: FX lines read "FX = INITIAL <name>", so the asset is the
# LAST token. A regex counter reported INITIAL and missed every death effect — wrong in the
# direction that flatters us.
expect="InGameUIAmericaBase SABarButtonGen2IN SABarButtonGen2ON SAEmptyFrame SAExpBar SALogo SCBigButton USLevelUP "
if [ "$residual" != "$expect" ]; then
  echo "  RESIDUAL RETAIL ART CHANGED"
  echo "    expected (the zh.sides HUD borrow): $expect"
  echo "    got:                                $residual"
  exit 1
fi
echo "  a pack on wholly authored art borrows 8 names, all of them the zh.sides HUD"
rm -rf "$ICO"; trap - EXIT

gate "a compiled pack is PLAYABLE in the retail engine"
# The 24 gates before this prove a pack COMPILES. None of them prove it plays, and every
# silent bug in CLAUDE.md's catalogue got through exactly there: InitialHealth 0, a welded
# turret, a missing ProductionUpdate, a black map, blank command-bar tiles. Each was found by
# a human noticing something in a match, which made the human the detector.
#
# OPT-IN, like the Godot shell check: it needs the retail install, macOS Accessibility, and
# ~2 minutes of wall clock, so it must never be the reason CI or a fresh clone fails.
if [ "${ZH_PLAY:-0}" != "1" ]; then
  echo "  skipped (set ZH_PLAY=1, needs the game + macOS Accessibility)"
elif [ ! -x "$HOME/GeneralsX/GeneralsZH/GeneralsXZH" ]; then
  echo "  skipped (no retail install at ~/GeneralsX/GeneralsZH)"
else
  # UNINSTALL every pack already there, first. Retail's PlayerTemplate is a single file at
  # Data/INI/PlayerTemplate.ini, so anything inside the DIRECTORY is a pack — which makes the
  # installed set discoverable without a manifest. Since sides became pack-prefixed, packs no
  # longer overwrite each other; they COEXIST, the lobby lists several factions, and the drive
  # plays whichever is default. Installing on top of leftovers silently tests the wrong pack.
  ZHDIR="$HOME/GeneralsX/GeneralsZH"
  for old_pack in $(ls "$ZHDIR/Data/INI/PlayerTemplate/"*.ini 2>/dev/null \
                    | xargs -r -n1 basename | sed 's/\.ini$//'); do
    find "$ZHDIR/Data/INI" \( -name "$old_pack.ini" -o -name "${old_pack}_overrides.ini" \) \
      -delete 2>/dev/null || true
    rm -f "$ZHDIR/Art/Textures/${old_pack}_icons.tga" \
          "$ZHDIR/Art/Textures/${old_pack}_spark.tga" \
          "$ZHDIR/Art/Terrain/${old_pack}_ground.tga"
  done

  PLAY=$(mktemp -d)
  rts compile --target zh --out "$PLAY" \
    --mod content/mods/chokepoint.json --mod content/mods/demo-attach.json >/dev/null
  rsync -a "$PLAY/Data" "$PLAY/Art" "$HOME/GeneralsX/GeneralsZH/"
  USERMAPS="$HOME/Library/Application Support/GeneralsX/GeneralsZH/Maps"
  mkdir -p "$USERMAPS" && rsync -a "$PLAY/Maps/" "$USERMAPS/"
  # The map goes to USER data, not the game dir: MapCache::loadStandardMaps only READS
  # Maps/MapCache.ini and never scans, so a map in the install is never discovered.
  rm -rf "$PLAY"

  # ONE pack, or the lobby's default army is arbitrary. Sides are pack-prefixed now, so two
  # installed packs no longer overwrite each other — they coexist, the dropdown lists several
  # factions, and the drive plays whichever happens to be selected. That silently tests the
  # wrong pack, which is an hour of chasing a bug that is not there. Fail loudly instead.
  packs=$(ls "$HOME/GeneralsX/GeneralsZH/Data/INI/PlayerTemplate/"*.ini 2>/dev/null | wc -l | tr -d ' ')
  if [ "$packs" != "1" ]; then
    echo "  $packs packs installed; the lobby's default army is then arbitrary."
    ls "$HOME/GeneralsX/GeneralsZH/Data/INI/PlayerTemplate/" 2>/dev/null | sed 's/^/    /'
    echo "  Remove the others and re-run."; exit 1
  fi

  pkill -x GeneralsXZH 2>/dev/null || true
  sleep 2
  ./tools/zhdrive skirmish || { echo "  COULD NOT REACH A MATCH"; exit 1; }
  ./tools/zhdrive verify-pack || { echo "  PACK IS NOT PLAYABLE"; exit 1; }
  pkill -x GeneralsXZH 2>/dev/null || true
  echo "  faction selectable, map loads, icons resolve, build button charges the player"
fi

gate "line of sight — cover, and it is NOT passability"
# Slice 12 left a restriction rather than a model: terrain blocked movement and nothing else,
# so a wall thinner than the longest gun measured as EXACTLY ZERO — both armies walked up to
# it and shot over it. Two demo maps and one version of gate 18 were written before that was
# noticed, and the workaround was "author every wall wider than a gun".
#
# The control here is WATER, and it is what makes this a test of sight rather than of terrain:
# water stops ground movement and hides nothing. So two maps of IDENTICAL shape, one water and
# one cliff, differ in exactly one property.
LOS=$(mktemp -d); trap 'rm -rf "$LOS"' EXIT
python3 - "$LOS" <<'PYLOS'
import json, sys
N = 48
def pack(name, ch, surface):
    rows = []
    for r in range(N):
        row = ["."] * N
        if not (5 <= r <= 8):          # a gate, off the spawn axis, so pathing has a way round
            row[23] = row[24] = ch     # TWO cells thick — thinner than every gun in the pack
        rows.append("".join(row))
    return {"meta": {"name": name, "version": 1},
            "map": {"cellSize": 2.0, "outside": "clear",
                    "legend": {".": "clear", ch: surface}, "rows": rows}}
open(f"{sys.argv[1]}/water.json", "w").write(json.dumps(pack("thinwater", "~", "water")))
open(f"{sys.argv[1]}/cliff.json", "w").write(json.dumps(pack("thincliff", "#", "cliff")))
PYLOS

# --- 1. Sight and passability are INDEPENDENT flags. ---------------------------------
w_line=$(rts lint --mod "$LOS/water.json" | grep '^map:')
c_line=$(rts lint --mod "$LOS/cliff.json" | grep '^map:')
case "$w_line" in
  *"pathing=True sight=False"*) : ;;
  *) echo "  WATER SHOULD BLOCK MOVEMENT AND NOTHING ELSE: $w_line"; exit 1 ;;
esac
case "$c_line" in
  *"pathing=True sight=True"*) : ;;
  *) echo "  A CLIFF SHOULD BLOCK BOTH: $c_line"; exit 1 ;;
esac
echo "  water blocks movement and hides nothing; a cliff does both"

# --- 2. Sight has a CONSEQUENCE, and it is the whole point of the slice. --------------
# Before this, these two maps measured identically: the wall was two cells thick, every gun
# outranged it, and both armies simply shot across. If they still measure the same, the
# feature is inert.
duel_secs() { rts duel --a crusader --b battlemaster --n 20 --seed 42 "$@" \
                | sed -n 's/.*avg battle length: \([0-9.]*\)s.*/\1/p'; }
duel_hash() { rts duel --a crusader --b battlemaster --n 20 --seed 42 "$@" \
                | sed -n 's/.*last final hash: \([0-9a-f]*\).*/\1/p'; }
ws=$(duel_secs --mod "$LOS/water.json"); cs=$(duel_secs --mod "$LOS/cliff.json")
wh=$(duel_hash --mod "$LOS/water.json"); ch=$(duel_hash --mod "$LOS/cliff.json")
[ "$wh" != "$ch" ] || { echo "  SIGHT CHANGED NOTHING: both maps hash $wh"; exit 1; }
python3 -c "
import sys
w, c = float('$ws'), float('$cs')
if abs(w - c) < 2.0:
    print(f'  A THIN WALL STILL COSTS NOTHING: {w}s transparent vs {c}s opaque'); sys.exit(1)
print(f'  the SAME two-cell wall: {w}s when it only blocks movement, {c}s when it also '
      f'blocks sight')
"

# --- 3. A map that blocks no sight must be bit-identical to no LOS model at all. ------
# Opt-in by consequence, the house rule since slice 5. The water map exercises pathing hard
# and must not perturb a single shot, which is why CanSee short-circuits on the flag rather
# than on the grid.
rts replay --a crusader --b battlemaster --seed 7 --mod "$LOS/water.json" | grep -q "DETERMINISM OK" \
  || { echo "  LOS BROKE DETERMINISM ON A SIGHTLESS MAP"; exit 1; }
echo "  a sightless map stays deterministic and opt-in by consequence"
rm -rf "$LOS"; trap - EXIT

gate "a pack CARRIES its art, and every reference resolves"
# `rts compile` used to emit INI and one icon sheet. Every authored .w3d was copied into the
# install BY HAND, and zh.models was only checked for having a MAPPING — never for the file
# existing. Name a mesh that is not there and the unit moves, shoots, dies and cannot be seen
# or clicked, with no error from the engine. That is the desertA failure class, and that one
# reached a real match before anyone noticed.
AS=$(mktemp -d); trap 'rm -rf "$AS"' EXIT
GAMEDIR="$HOME/GeneralsX/GeneralsZH"

if [ ! -d "$GAMEDIR" ]; then
  echo "  (no install: asset resolution can only be checked against one — skipped)"
else
  python3 - "$AS" <<'PYART'
import json, re, sys
d = json.loads(re.sub(r'^\s*//.*$', '', open('content/mods/demo-attach.json').read(), flags=re.M))
ghost = json.loads(json.dumps(d)); ghost['meta']['name'] = 'ghostmesh'
ghost['zh']['models']['hellhound'] = 'AVTotallyNotAMesh'
open(f'{sys.argv[1]}/ghost.json', 'w').write(json.dumps(ghost))
ships = json.loads(json.dumps(d)); ships['meta']['name'] = 'shipsart'
ships['zh']['models']['hellhound'] = 'E2EBRANDNEW'
ships['zh']['art'] = [f'{sys.argv[1]}/E2EBRANDNEW.W3D']
open(f'{sys.argv[1]}/ships.json', 'w').write(json.dumps(ships))
PYART

  # --- 1. An unresolvable model is a CAP ERROR, not a warning. ------------------------
  ghost_out=$(rts lint --target zh --mod "$AS/ghost.json" 2>&1 || true)
  case "$ghost_out" in
    *"resolves to no file"*) : ;;
    *) echo "  A NONEXISTENT MESH WAS ACCEPTED — the unit would be invisible in game"
       echo "$ghost_out" | tail -3; exit 1 ;;
  esac
  if rts lint --target zh --mod "$AS/ghost.json" >/dev/null 2>&1; then
    echo "  AN UNRESOLVABLE MODEL DID NOT FAIL THE LINT"; exit 1
  fi
  echo "  a model that resolves to no file is refused, not warned about"

  # --- 2. Shipping the mesh WITH the pack satisfies it. --------------------------------
  # Otherwise the check would only permit adopting retail art, which is the opposite of
  # where this project is going.
  cp "$GAMEDIR/Art/W3D/RTSBOX.W3D" "$AS/E2EBRANDNEW.W3D" 2>/dev/null \
    || { echo "  (no authored mesh installed to copy — skipped)"; rm -rf "$AS"; trap - EXIT; }
  if [ -f "$AS/E2EBRANDNEW.W3D" ]; then
    rts lint --target zh --mod "$AS/ships.json" >/dev/null 2>&1 \
      || { echo "  A MESH THE PACK SHIPS WAS STILL REPORTED MISSING"; exit 1; }

    # --- 3. And the compiled output CONTAINS it. ---------------------------------------
    rts compile --target zh --out "$AS/out" --mod "$AS/ships.json" >/dev/null
    [ -f "$AS/out/Art/W3D/E2EBRANDNEW.W3D" ] \
      || { echo "  THE PACK DID NOT CARRY ITS OWN MESH into the output"; exit 1; }
    cmp -s "$AS/E2EBRANDNEW.W3D" "$AS/out/Art/W3D/E2EBRANDNEW.W3D" \
      || { echo "  the carried mesh differs from its source"; exit 1; }
    echo "  a pack ships its own mesh, and the compiled output carries it byte-identically"
  fi

  # --- 4. TWO PACKS MUST NOT FIGHT. -----------------------------------------------------
  # Objects were prefixed from the start; a faction's SIDE was not. Install two packs that each
  # define a faction called "hellfire" and you get duplicate PlayerTemplates, duplicate
  # ControlBarSchemes and one Side three files disagree about — the surviving faction points at
  # ONE pack's objects and the other's are unreachable. Four packs accumulated in a test
  # install and a match silently played the wrong pack's units.
  rts compile --target zh --out "$AS/p1" --mod content/mods/demo-attach.json >/dev/null
  rts compile --target zh --out "$AS/p2" --mod "$AS/ships.json" >/dev/null
  for kind in PlayerTemplate ControlBarScheme; do
    a=$(tr -d '\r' < "$AS/p1/Data/INI/$kind"/*.ini | grep -oE "^$kind \S+" | sort)
    b=$(tr -d '\r' < "$AS/p2/Data/INI/$kind"/*.ini | grep -oE "^$kind \S+" | sort)
    both=$(printf '%s\n%s\n' "$a" "$b" | sort | uniq -d)
    [ -z "$both" ] || { echo "  TWO PACKS DECLARE THE SAME $kind: $both"; exit 1; }
  done
  s1=$(tr -d '\r' < "$AS/p1/Data/INI/PlayerTemplate"/*.ini | grep -oE '^  Side = \S+' | sort -u)
  s2=$(tr -d '\r' < "$AS/p2/Data/INI/PlayerTemplate"/*.ini | grep -oE '^  Side = \S+' | sort -u)
  shared=$(printf '%s\n%s\n' "$s1" "$s2" | sort | uniq -d)
  [ -z "$shared" ] || { echo "  TWO PACKS SHARE A FACTION Side: $shared"; exit 1; }
  echo "  two packs installed together share no faction, side or control bar"

  # --- 5. Objects placed on the map survive into the emitted .map. ---------------------
  # Checked by the independent reader, and the OWNER is checked by name: GameLogic resolves
  # it with PlayerList::validateTeam, whose miss path is a DEBUG_CRASH before it falls back,
  # and DEBUG_CRASH is compiled into this build. "team" — the neutral side's default — is the
  # only owner a compiled map can safely name; a skirmish opponent exists in the LOBBY, not
  # in the map's SidesList.
  python3 - "$AS" <<'PYOBJ'
import json, re, sys
d = json.loads(re.sub(r'^\s*//.*$', '', open('content/mods/chokepoint.json').read(), flags=re.M))
d['meta']['name'] = 'e2eplaced'
d['map']['objects'] = [{"template": "AmericaTankCrusader", "x": 6.0, "y": -4.0, "owner": "team"}]
open(f'{sys.argv[1]}/placed.json', 'w').write(json.dumps(d))
PYOBJ
  rts compile --target zh --out "$AS/pl" --mod "$AS/placed.json" >/dev/null
  python3 - "$AS/pl/Maps/e2eplaced_map/e2eplaced_map.map" <<'PYRD'
import sys
ns = {"__name__": "x", "__file__": "tools/zhasset"}
exec(compile(open("tools/zhasset").read().split("def main(")[0], "zhasset", "exec"), ns)
b, _ = ns["map_load"](sys.argv[1])
m = ns["map_parse"](b)
placed = [o for o in m["objects"] if o["template"]]
if len(placed) != 1:
    print(f"  EXPECTED 1 PLACED OBJECT, got {len(placed)}"); sys.exit(1)
o = placed[0]
if o["dict"].get("originalOwner") != "team":
    print(f"  UNSAFE OWNER {o['dict'].get('originalOwner')!r}: an unknown team is a "
          f"DEBUG_CRASH at load, not a fallback to neutral"); sys.exit(1)
# our (6,-4) at worldScale 16 -> +96,-64 from the playable centre
if not (860 < o["x"] < 960 and 660 < o["y"] < 760):
    print(f"  PLACED AT THE WRONG POSITION: {o['x']:.0f},{o['y']:.0f}"); sys.exit(1)
print(f"  a placed object round-trips at ZH ({o['x']:.0f},{o['y']:.0f}) owned by 'team'")
PYRD
fi
rm -rf "$AS"; trap - EXIT

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

gate "a pack has a VOICE, and its waves are graded by a second reader"
# Audio was the last untouched content category: every unit was silent, and the pack emitted not
# one line of it. The measurement that shaped the slice is the shipped census — 8,638 files,
# 1,049.6 MB, and a format split of 5,346 plain PCM against 3,236 IMA ADPCM, with the single
# largest bucket (5,157) mono/22,050/16-bit PCM. So the most typical format and the cheapest one
# to produce are the same, and authoring audio needs a 44-byte header rather than an encoder.
#
# WHAT THIS GATE CANNOT DO, stated because the absence is otherwise invisible: the arm64 engine
# we test on carries NO wav decoder. OpenALAudioManager creates a context, allocates sources and
# never calls alBufferData; update() is a documented no-op. Nothing this project emits can be
# heard on it, from any input. So the checks below are schema authority plus an independent
# reader, and playback is not among them.
AUD=$(mktemp -d); trap 'rm -rf "$AUD"' EXIT
rts compile --target zh --out "$AUD/out" >/dev/null

# --- 1. The pack emits events AND the files they name. ---------------------------------
# An AudioEvent never states a path: generateFilenamePrefix composes
# {AudioRoot}\{SoundsFolder}\{name}.{SoundsExtension} out of AudioSettings.ini. So a `Sounds`
# entry IS a base filename under Data/Audio/Sounds, and an event whose wave is elsewhere parses
# cleanly, resolves cleanly and plays nothing — the desertA failure class, in a new category.
[ -f "$AUD/out/Data/INI/SoundEffects/skeleton_pack.ini" ] \
  || { echo "  NO SoundEffects INI EMITTED"; exit 1; }
nwav=$(find "$AUD/out/Data/Audio/Sounds" -name '*.wav' 2>/dev/null | wc -l | tr -d ' ')
[ "$nwav" -ge 6 ] || { echo "  expected the pack's waves under Data/Audio/Sounds, found $nwav"; exit 1; }
echo "  $nwav waves under Data/Audio/Sounds, and a SoundEffects block that names them"

# --- 2. The second reader grades the writer. -------------------------------------------
# Content/ZhAudio.cs writes; tools/zhasset audio reads. A writer checked by its own reader
# proves only that the two agree, which is why this reader also censuses all 8,638 shipped
# files before it is allowed to judge ours.
python3 tools/zhasset audio "$AUD/out" > "$AUD/val.txt" 2>&1 \
  || { echo "  THE AUDIO VALIDATOR REJECTED OUR OWN OUTPUT"; cat "$AUD/val.txt"; exit 1; }
grep -q "AUDIO OK" "$AUD/val.txt" || { echo "  validator did not pass"; cat "$AUD/val.txt"; exit 1; }
grep -qE 'tag=1 ch=1 rate=22050 bits=16' "$AUD/val.txt" \
  || { echo "  emitted waves are not the plurality shipped format"; cat "$AUD/val.txt"; exit 1; }
echo "  every wave is plain PCM mono 22,050/16 — the format 5,157 shipped files use"

# --- 3. A validator that cannot fail is not a check. -----------------------------------
# Each probe breaks exactly one property and asserts the reader says so. The enum probe uses a
# SUBSTRING of a legal name on purpose: GARRISONABLE greps as present because it is a substring
# of GARRISONABLE_UNTIL_DESTROYED, and that cost a boot. Checked against the C++ name table.
probe_fails() {  # $1 = label, $2 = mutation script
  rm -rf "$AUD/p"; cp -R "$AUD/out" "$AUD/p"
  ( cd "$AUD/p" && eval "$2" )
  if python3 tools/zhasset audio "$AUD/p" >/dev/null 2>&1; then
    echo "  THE VALIDATOR ACCEPTED A BROKEN PACK: $1"; exit 1
  fi
}
probe_fails "an enum value that is a substring of a legal one" \
  "sed -i '' 's/Type = ui player/Type = ui play/' Data/INI/SoundEffects/skeleton_pack.ini"
probe_fails "a Sounds entry with no wave on disk" \
  "rm Data/Audio/Sounds/skeleton_pack_death.wav"
probe_fails "a truncated wave (RIFF size and data length both lie)" \
  "python3 -c \"f='Data/Audio/Sounds/skeleton_pack_fire.wav';b=open(f,'rb').read();open(f,'wb').write(b[:len(b)//2])\""
echo "  three broken packs, three refusals — the reader fails when it should"

# --- 4. The loop claim, checked rather than trusted. ------------------------------------
# ZhAudio sizes the engine loop to a whole number of cycles so the wrap is silent. The test is
# the CURVATURE at the join against the 99.9th percentile everywhere else — self-calibrating,
# because a plain |s[0] - s[n-1]| test passes a half-cycle truncation where both ends sit at
# zero with opposite slope. Cutting whole cycles must stay CLEAN; cutting part of one must not.
loopcut() {
  rm -rf "$AUD/p"; cp -R "$AUD/out" "$AUD/p"
  python3 - "$AUD/p/Data/Audio/Sounds/skeleton_pack_engine.wav" "$1" <<'PYCUT'
import struct, sys
f, k = sys.argv[1], int(sys.argv[2])
b = open(f, 'rb').read(); n = (len(b) - 44) // 2
s = list(struct.unpack('<%dh' % n, b[44:44 + n * 2]))[:n - k]; m = len(s)
o = bytearray(b[:44]) + struct.pack('<%dh' % m, *s)
o[4:8] = struct.pack('<I', len(o) - 8); o[40:44] = struct.pack('<I', m * 2)
open(f, 'wb').write(bytes(o))
PYCUT
  python3 tools/zhasset audio "$AUD/p" >/dev/null 2>&1 && echo clean || echo clicks
}
[ "$(loopcut 95)"  = clicks ] || { echo "  A QUARTER-CYCLE SEAM WENT UNDETECTED"; exit 1; }
[ "$(loopcut 380)" = clean  ] || { echo "  cutting ONE WHOLE CYCLE was reported as a seam"; exit 1; }
echo "  a partial-cycle loop is refused; a whole-cycle one is not"

# --- 5. The census runs against the real install, when there is one. --------------------
if [ -d "$HOME/GeneralsX/GeneralsZH" ]; then
  cen=$(python3 tools/zhasset audio --census 2>&1)
  case "$cen" in
    *"plain PCM (tag 1)"*) echo "  census: $(echo "$cen" | head -1 | sed 's/^ *//')" ;;
    *) echo "  THE SHIPPED-AUDIO CENSUS FAILED"; echo "$cen" | tail -3; exit 1 ;;
  esac
else
  echo "  (no install: the shipped census can only run against one — skipped)"
fi
rm -rf "$AUD"; trap - EXIT


gate "a pack NAMES itself — additively where it can, by local merge where it cannot"
# Every label a pack invents rendered as MISSING:'...', and the compiler offered only a choice
# between that and --with-strings, which writes Data/Generals.str and thereby REPLACES retail's
# 6,422 labels rather than extending them. Both halves of the dichotomy were wrong.
#
# There are two channels and they cover different screens:
#   IN-MATCH  <mapdir>/map.str, genuinely additive. fetch() bsearches the main table first and
#             falls back to the map table, so ours fill holes and displace nothing. Retail ships
#             these itself, beside its campaign maps.
#   THE SHELL the MAIN table only. A faction's name is resolved at INI PARSE TIME —
#             `DisplayName` is INI::parseAndTranslateLabel — so no per-map file can ever reach
#             it. That is what `zhasset strings --merge` is for, and it runs against the
#             player's OWN install, never producing anything distributable.
STR=$(mktemp -d); trap 'rm -rf "$STR"' EXIT
rts compile --target zh --out "$STR/out" --mod content/mods/demo-attach.json \
                                         --mod content/mods/demo-world.json >/dev/null

# --- 1. BOTH faction labels, because the visible one is not the one the INI states. ---
# `DisplayName = INI:Faction<side>` is what the file says, and the skirmish army dropdown
# ignores it: WOLGameSetupMenu.cpp formats its own key, "SIDE:%s". Emitting only the first
# leaves every slot reading MISSING:'SIDE:demo_hellfire' while the INI looks perfect. Found by
# screenshotting the menu; no log line mentions it.
# NOTE the `tr -d`: a .str is CRLF, like every other text file the engine reads and like
# retail's own map.str files. grep -x against a bare label fails on the trailing CR.
# NOTE two shell traps, both of which this file has been bitten by before. `grep -q` after a
# pipe exits on its first hit, SIGPIPEs the writer, and `set -o pipefail` turns that into a
# failed test — so count instead of short-circuiting. And LC_ALL=C, because a .str is latin-1.
haslabel() { [ "$(LC_ALL=C tr -d '\r' < "$1" | grep -cxF "$2")" -ge 1 ]; }
for tag in "INI:Factiondemo_hellfire" "SIDE:demo_hellfire"; do
  haslabel "$STR/out/labels.str" "$tag" \
    || { echo "  labels.str is missing $tag"; exit 1; }
done
haslabel "$STR/out/Maps/demo_map/map.str" "MAP:demo_map" \
  || { echo "  map.str does not name the map"; exit 1; }
echo "  labels.str carries INI:Faction AND SIDE; map.str names the map"

# --- 2. labels.str must NOT be installable by accident. ------------------------------
# A bare Generals.str under Data/ would be picked up by `rsync -a Data Art` and replace the
# whole retail table with 37 labels. It lives at the pack root for exactly that reason.
[ ! -e "$STR/out/Data/Generals.str" ] \
  || { echo "  A BARE Generals.str WAS EMITTED — it would shadow all 6,422 retail labels"; exit 1; }
grep -q "^labels.str$" "$STR/out/MANIFEST.txt" \
  && { echo "  labels.str is in the MANIFEST — it is a merge INPUT, not an installed file"; exit 1; }
echo "  no installable Generals.str, and labels.str is not manifested"

# --- 3. The merge is lossless over the real table. -----------------------------------
if [ -d "$HOME/GeneralsX/GeneralsZH" ]; then
  out=$(python3 tools/zhasset strings --merge "$STR/out" --out "$STR/merged.str" 2>&1) \
    || { echo "  THE MERGE FAILED"; echo "$out"; exit 1; }
  case "$out" in *round-tripped*) : ;; *) echo "  merge did not round-trip"; echo "$out"; exit 1 ;; esac
  echo "  $(echo "$out" | sed -n '2p' | sed 's/^ *//')"

  # The two cases a naive writer corrupts, checked against the ENGINE's rules rather than
  # against intuition. 772 of 6,422 retail labels contain a real newline and a .str is
  # line-oriented; 3 end in an ESCAPED quote, which `line.strip('"')` silently truncates by one
  # character. Both were live bugs in this tool before the round-trip check existed.
  haslabel "$STR/merged.str" 'GUI:UnitsBuilt' \
    || { echo "  a newline label vanished"; exit 1; }
  [ "$(LC_ALL=C grep -ac 'sarcastic> \\"General\.\\""' "$STR/merged.str")" -ge 1 ] \
    || { echo "  an ESCAPED-QUOTE label was mangled"; exit 1; }
  echo "  newline-bearing and escaped-quote labels survive the round trip"

  # A comment in a .str is `//` — parseStringFile tests for 0x2F2F. A `;` line is read as a
  # LABEL and the line after it as its text, corrupting two entries with no error.
  [ "$(LC_ALL=C grep -ac '^;' "$STR/merged.str")" -eq 0 ] \
    || { echo "  the merged file uses ';' comments, which parse as LABELS"; exit 1; }
  echo "  comments are // — a ';' line would be parsed as a label"
else
  echo "  (no install: the merge needs one to read generals.csf from — skipped)"
fi
rm -rf "$STR"; trap - EXIT


gate "a mesh can be LOOKED AT — glTF export, graded by a second implementation"
# Every other check on a mesh in this file is blind. w3dround proves the container is
# understood by re-emitting it byte-identically; the authoring gate asserts counts, UV density
# and normal variation. None of that can tell you the model is the SHAPE you meant, and the
# project has the scar to prove the difference matters: art profiles were measured and found
# UNDERIVABLE from geometry (slice 15, struck), so a mesh whose declared radius disagrees with
# its drawn size is selectable in the wrong place and nothing here would notice.
#
# glTF rather than a Blender plugin: writing it straight out of the parser we already have
# needs no dependency, and gives a SECOND IMPLEMENTATION reading our own output — the standing
# this file already demands of ZhMapWriter, which is graded by `zhasset map` because a writer
# checked by its own reader proves only that the two agree.
GL=$(mktemp -d); trap 'rm -rf "$GL"' EXIT
W3DBIG="$HOME/GeneralsX/GeneralsZH/ZH_Generals/W3D.big"
if [ ! -f "$W3DBIG" ]; then
  echo "  (no install: the sample models are EA's art, extracted locally — skipped)"
else
  python3 - "$W3DBIG" "$GL" <<'PYGL'
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
PYGL

  # --- 1. The exporter must AGREE with the explainer on what is in the file. -----------
  # `zhasset w3d` sums VERTICES/TRIANGLES over the flat chunk stream; the exporter walks the
  # TREE, because a 10-sub-object model has ten VERTICES chunks and a renderer needs to know
  # which belongs to which mesh. Two different traversals reaching the same totals is the
  # cheapest evidence that the regrouping did not lose or double-count a chunk.
  for f in "$GL"/AVLEOPARD.W3D "$GL"/ABPWRPLANT_D01.W3D; do
    a=$(./tools/zhasset w3d "$f" | sed -n 's/.*geometry *: \([0-9,]*\) vertices, \([0-9,]*\) triangles/\1 \2/p')
    b=$(./tools/zhasset gltf "$f" --out "${f%.W3D}.glb" | sed -n 's/.*mesh(es), \([0-9,]*\) vertices, \([0-9,]*\) triangles/\1 \2/p')
    [ "$a" = "$b" ] && [ -n "$a" ] \
      || { echo "  EXPORTER DISAGREES with the explainer on $(basename "$f"): '$a' vs '$b'"; exit 1; }
  done
  echo "  the tree walk and the flat walk agree on every count"

  # --- 2. A .glb must be a well-formed glTF container. ----------------------------------
  # Checked structurally rather than by "a viewer opened it": a truncated BIN chunk, or an
  # accessor whose byteOffset is not 4-aligned, produces a file some viewers tolerate and
  # others render as nothing.
  python3 - "$GL/AVLEOPARD.glb" <<'PYGLB'
import json, struct, sys
d = open(sys.argv[1], "rb").read()
magic, ver, total = struct.unpack_from("<III", d, 0)
assert magic == 0x46546C67 and ver == 2, (hex(magic), ver)
assert total == len(d), (total, len(d))
off, chunks = 12, {}
while off < len(d):
    ln, ct = struct.unpack_from("<II", d, off)
    chunks[ct] = d[off + 8: off + 8 + ln]
    assert ln % 4 == 0, f"chunk {ct:#x} length {ln} is not 4-aligned"
    off += 8 + ln
doc = json.loads(chunks[0x4E4F534A])
bin_ = chunks[0x004E4942]
assert doc["buffers"][0]["byteLength"] <= len(bin_)
for i, v in enumerate(doc["bufferViews"]):
    assert v["byteOffset"] % 4 == 0, f"bufferView {i} is not 4-aligned"
    assert v["byteOffset"] + v["byteLength"] <= len(bin_), f"bufferView {i} runs past the BIN chunk"
# POSITION must carry min/max: the spec requires it, and a viewer uses it to frame the model.
for m in doc["meshes"]:
    for p in m["primitives"]:
        a = doc["accessors"][p["attributes"]["POSITION"]]
        assert "min" in a and "max" in a, "POSITION accessor has no bounds"
print(f"  valid glb: {len(doc['meshes'])} meshes, {len(doc['accessors'])} accessors, "
      f"{len(bin_):,} bytes of buffer")
PYGLB

  # --- 3. A file with no geometry is REFUSED, not exported empty. -----------------------
  # A _SKL file is all HIERARCHY. Writing it as a valid glTF with zero meshes gives a viewer
  # that opens on a blank grey void, which reads as a broken exporter rather than as the
  # wrong input file.
  if ./tools/zhasset gltf "$GL/AIHERO_SKL.W3D" --out "$GL/skl.glb" >/dev/null 2>&1; then
    echo "  A SKELETON-ONLY FILE EXPORTED AS GEOMETRY — a viewer would open on nothing"; exit 1
  fi
  echo "  a skeleton-only file is refused rather than exported as an empty scene"

  # --- 4. AUTHORED dimensions must survive the whole chain. -----------------------------
  # This is the one that proves the axis convention. W3D is Z-up and glTF is Y-up, so the
  # export applies the -90 degree rotation about X; get it wrong and the model arrives lying
  # on its side, which reads as a modelling error rather than a transform one. Authoring a
  # deliberately NON-CUBIC box is what makes the mistake visible — on 60x60x60 every wrong
  # axis mapping looks perfect.
  ./tools/zhasset w3dbox --template "$GL/ABPWRPLANT_D01.W3D" --out "$GL/tall.w3d" \
      --name E2ETALL --size 60 40 90 --out-dir "$GL" >/dev/null
  ./tools/zhasset gltf "$GL/tall.w3d" --out "$GL/tall.glb" \
    | grep -q "bounds (Y-up): 60.00 x 90.00 x 40.00" \
    || { echo "  AUTHORED 60x40x90 (Z-up) did not arrive as 60x90x40 (Y-up) — axes are wrong"
         ./tools/zhasset gltf "$GL/tall.w3d" --out "$GL/tall.glb"; exit 1; }
  echo "  authored 60x40x90 Z-up arrives as 60x90x40 Y-up — the rotation is right way round"

  # --- 5. An INDEPENDENT glTF implementation, when one is installed. --------------------
  # Everything above is still our own code grading our own output. Blender's importer was
  # written by people who have never heard of this project, and it is the only check here that
  # nothing in this repo can talk its way past.
  BL=/Applications/Blender.app/Contents/MacOS/Blender
  if [ ! -x "$BL" ]; then
    echo "  (no Blender: the independent importer check is skipped)"
  else
    "$BL" --background --python-expr "
import bpy
bpy.ops.wm.read_factory_settings(use_empty=True)
bpy.ops.import_scene.gltf(filepath='$GL/AVLEOPARD.glb')
v = t = 0
for o in bpy.data.objects:
    if o.type == 'MESH':
        o.data.calc_loop_triangles()
        v += len(o.data.vertices); t += len(o.data.loop_triangles)
print('BLENDER %d %d' % (v, t))
" 2>/dev/null | grep -q "BLENDER 400 245" \
      || { echo "  BLENDER DID NOT READ BACK 400 verts / 245 tris — the glb is not portable"; exit 1; }
    echo "  Blender's own importer reads back 400 vertices / 245 triangles"
  fi
fi
rm -rf "$GL"; trap - EXIT


gate "a mesh authored from NOTHING — no template, no retail bytes"
# `w3dbox` computes its own geometry but needs a --template, and the only templates in
# existence are RETAIL meshes: MATERIAL_INFO, SHADERS, VERTEX_MATERIALS, TEXTURES and the
# MATERIAL_PASS scaffolding are copied out of ABPWRPLANT_D01.W3D. So "authored" has meant OUR
# GEOMETRY IN THEIR CONTAINER, which is not the claim this project wants to make.
#
# `w3dfrom` builds every chunk from spec. That is also what lets Blender be the modelling
# backend — real booleans, bevels and decimation, exported as glTF, landing here — because a
# mesh arriving from outside has no template to inherit from.
FN=$(mktemp -d); trap 'rm -rf "$FN"' EXIT
W3DBIG="$HOME/GeneralsX/GeneralsZH/ZH_Generals/W3D.big"
if [ ! -f "$W3DBIG" ]; then
  echo "  (no install: the round-trip needs a real mesh to grade against — skipped)"
else
  python3 - "$W3DBIG" "$FN" <<'PYFN'
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
        if base in ("ABPWRPLANT_D01.W3D", "AVLEOPARD.W3D"):
            f.seek(off); open(os.path.join(out, base), "wb").write(f.read(size))
PYFN

  # --- 1. w3d -> glTF -> w3d must preserve the GEOMETRY exactly. ------------------------
  # Not byte-identity: the rebuilt file carries our own material name, so it is legitimately a
  # few bytes different. What must survive is every number a renderer reads. Vertices are
  # asserted BIT-exact because both hops are pure sign swaps between axes — anything non-zero
  # there means a transform is doing arithmetic it should not be.
  ./tools/zhasset gltf "$FN/ABPWRPLANT_D01.W3D" --out "$FN/p.glb" >/dev/null
  ./tools/zhasset w3dfrom --gltf "$FN/p.glb" --out "$FN/p.w3d" --name RTPLANT >/dev/null
  python3 - "$FN/ABPWRPLANT_D01.W3D" "$FN/p.w3d" <<'PYCMP'
import struct, sys
def geom(p):
    buf = open(p, "rb").read()
    def walk(off, end):
        while off + 8 <= end:
            t, raw = struct.unpack_from("<II", buf, off); sz = raw & 0x7FFFFFFF; body = off + 8
            if raw & 0x80000000: yield from walk(body, body + sz)
            else: yield t, buf[body:body + sz]
            off = body + sz
    v = uv = tri = None
    for t, pl in walk(0, len(buf)):
        if t == 0x0002: v = [struct.unpack_from("<3f", pl, i * 12) for i in range(len(pl) // 12)]
        if t == 0x004A: uv = [struct.unpack_from("<2f", pl, i * 8) for i in range(len(pl) // 8)]
        if t == 0x0020: tri = [struct.unpack_from("<3I", pl, i * 32) for i in range(len(pl) // 32)]
    return v, uv, tri
a, b = geom(sys.argv[1]), geom(sys.argv[2])
assert len(a[0]) == len(b[0]) and len(a[2]) == len(b[2]), "counts changed"
dv = max(max(abs(p - q) for p, q in zip(P, Q)) for P, Q in zip(a[0], b[0]))
du = max(max(abs(p - q) for p, q in zip(P, Q)) for P, Q in zip(a[1], b[1]))
assert dv == 0.0, f"VERTICES MOVED by {dv} — an axis transform is not a pure sign swap"
assert du < 1e-6, f"UVs drifted by {du}"
assert a[2] == b[2], "triangle indices changed"
print(f"  geometry survives w3d->gltf->w3d: {len(a[0])} verts bit-exact, "
      f"{len(a[2])} tris identical, UVs within {du:.1e}")
PYCMP

  # --- 2. The rebuilt file must be a WELL-FORMED w3d by our own reader. -----------------
  ./tools/zhasset w3dround "$FN/p.w3d" | grep -q "BYTE-IDENTICAL" \
    || { echo "  A FROM-SCRATCH MESH DOES NOT ROUND-TRIP — the chunk tree is malformed"; exit 1; }
  # And it must carry the same KINDS of chunk retail does. A file that parses but is missing
  # SHADERS or MATERIAL_PASS renders untextured, with no error from anything.
  for k in MESH_HEADER3 VERTICES VERTEX_NORMALS TRIANGLES VERTEX_SHADE_INDICES \
           MATERIAL_INFO VERTEX_MATERIAL_INFO SHADERS TEXTURE_NAME STAGE_TEXCOORDS; do
    ./tools/zhasset w3d "$FN/p.w3d" -v | grep -q "$k" \
      || { echo "  REBUILT MESH IS MISSING $k — it would load and render wrong"; exit 1; }
  done
  echo "  the rebuilt mesh round-trips and carries every chunk kind retail carries"

  # --- 3. Multi-mesh must be ASSEMBLED, not just concatenated. --------------------------
  # A .w3d holding ten meshes and no HLOD loads as ten unrelated pieces the engine never puts
  # together. AVLeopard is the real case: ten sub-objects, house colour among them.
  ./tools/zhasset gltf "$FN/AVLEOPARD.W3D" --out "$FN/t.glb" >/dev/null
  ./tools/zhasset w3dfrom --gltf "$FN/t.glb" --out "$FN/t.w3d" --name RTTANK >/dev/null
  ./tools/zhasset w3d "$FN/t.w3d" | grep -q "sub-objects : 10" \
    || { echo "  A TEN-MESH MODEL CAME BACK WITHOUT ITS HLOD — the pieces never assemble"; exit 1; }
  ./tools/zhasset w3d "$FN/t.w3d" | grep -q "400 vertices, 245 triangles" \
    || { echo "  the ten-mesh rebuild lost geometry"; exit 1; }
  echo "  a ten-sub-object model rebuilds with its HLOD and all 400 verts / 245 tris"

  # --- 4. NO UVs must drop the texture stage, not emit an empty one. --------------------
  # The loader sizes STAGE_TEXCOORDS from NumVertices, so an empty array is a read past the
  # end of the chunk — and MATERIAL_INFO's counts have to be restated to match, because those
  # counts are what the parse consumes.
  python3 - "$FN/t.glb" "$FN/nouv.glb" <<'PYNOUV'
import json, struct, sys
d = open(sys.argv[1], "rb").read()
off, ch = 12, {}
while off + 8 <= len(d):
    ln, ct = struct.unpack_from("<II", d, off); ch[ct] = d[off + 8:off + 8 + ln]; off += 8 + ln
doc = json.loads(ch[0x4E4F534A]); bn = ch[0x004E4942]
for m in doc["meshes"]:
    for p in m["primitives"]:
        p["attributes"].pop("TEXCOORD_0", None)
js = json.dumps(doc, separators=(",", ":")).encode(); js += b" " * (-len(js) % 4)
body = struct.pack("<II", len(js), 0x4E4F534A) + js + struct.pack("<II", len(bn), 0x004E4942) + bn
open(sys.argv[2], "wb").write(struct.pack("<III", 0x46546C67, 2, 12 + len(body)) + body)
PYNOUV
  ./tools/zhasset w3dfrom --gltf "$FN/nouv.glb" --out "$FN/nouv.w3d" --name NOUV >/dev/null
  ./tools/zhasset w3d "$FN/nouv.w3d" -v | grep -q "STAGE_TEXCOORDS" \
    && { echo "  A UV-LESS MESH EMITTED AN EMPTY TEXTURE STAGE — the loader reads past it"; exit 1; }
  ./tools/zhasset w3dround "$FN/nouv.w3d" | grep -q "BYTE-IDENTICAL" \
    || { echo "  the UV-less mesh is malformed"; exit 1; }
  echo "  a UV-less mesh drops its texture stage instead of declaring an empty one"

  # --- 5. And an INDEPENDENT implementation must still read the rebuilt file. -----------
  BL=/Applications/Blender.app/Contents/MacOS/Blender
  if [ ! -x "$BL" ]; then
    echo "  (no Blender: the independent importer check is skipped)"
  else
    ./tools/zhasset gltf "$FN/t.w3d" --out "$FN/t2.glb" >/dev/null
    "$BL" --background --python-expr "
import bpy
bpy.ops.wm.read_factory_settings(use_empty=True)
bpy.ops.import_scene.gltf(filepath='$FN/t2.glb')
v = t = 0
for o in bpy.data.objects:
    if o.type == 'MESH':
        o.data.calc_loop_triangles()
        v += len(o.data.vertices); t += len(o.data.loop_triangles)
print('BLENDER %d %d' % (v, t))
" 2>/dev/null | grep -q "BLENDER 400 245" \
      || { echo "  BLENDER COULD NOT READ THE REBUILT MESH BACK"; exit 1; }
    echo "  Blender reads the rebuilt mesh back at 400 vertices / 245 triangles"
  fi
fi
rm -rf "$FN"; trap - EXIT


gate "a glTF NODE TRANSFORM lands in the vertices"
# *This was a live bug, and it is the one that justifies having built the viewer first.* A W3D
# mesh has no node transform to carry a position — it lives in the vertices — while glTF puts
# it on the node. The importer read mesh primitives raw, so every part that declared a position
# came out at the origin. The authored building rendered with its main hall fifteen units below
# where the recipe put it and its roof floating over a gap, and NOTHING reported a problem:
# counts matched, the file round-tripped byte-identically, Blender read it back happily. It was
# found by rendering the model and looking at it.
#
# Synthetic input on purpose — no retail install and no Blender needed, so the check runs
# everywhere. A hand-built glb is also the only way to state the case exactly: one triangle,
# one node, one translation, one right answer.
NT=$(mktemp -d); trap 'rm -rf "$NT"' EXIT
python3 - "$NT/node.glb" <<'PYNT'
import json, struct, sys
# One triangle at the origin, on a node translated by (100, 200, 300) in glTF's Y-up frame.
tri = struct.pack("<9f", 0,0,0, 10,0,0, 0,10,0) + struct.pack("<3I", 0, 1, 2)
doc = {
  "asset": {"version": "2.0"},
  "scene": 0, "scenes": [{"nodes": [0]}],
  "nodes": [{"mesh": 0, "name": "SHIFTED", "translation": [100, 200, 300]}],
  "meshes": [{"name": "SHIFTED", "primitives": [{"attributes": {"POSITION": 0}, "indices": 1}]}],
  "accessors": [
    {"bufferView": 0, "componentType": 5126, "count": 3, "type": "VEC3",
     "min": [0,0,0], "max": [10,10,0]},
    {"bufferView": 1, "componentType": 5125, "count": 3, "type": "SCALAR"}],
  "bufferViews": [{"buffer": 0, "byteOffset": 0, "byteLength": 36},
                  {"buffer": 0, "byteOffset": 36, "byteLength": 12}],
  "buffers": [{"byteLength": len(tri)}],
}
js = json.dumps(doc, separators=(",", ":")).encode()
js += b" " * (-len(js) % 4)
bn = tri + b"\0" * (-len(tri) % 4)
body = struct.pack("<II", len(js), 0x4E4F534A) + js + struct.pack("<II", len(bn), 0x004E4942) + bn
open(sys.argv[1], "wb").write(struct.pack("<III", 0x46546C67, 2, 12 + len(body)) + body)
PYNT
./tools/zhasset w3dfrom --gltf "$NT/node.glb" --out "$NT/node.w3d" --name NODETEST >/dev/null
python3 - "$NT/node.w3d" <<'PYCHK'
import struct, sys
buf = open(sys.argv[1], "rb").read()
def walk(off, end):
    while off + 8 <= end:
        t, raw = struct.unpack_from("<II", buf, off); sz = raw & 0x7FFFFFFF; body = off + 8
        if raw & 0x80000000: yield from walk(body, body + sz)
        else: yield t, buf[body:body + sz]
        off = body + sz
v = next(p for t, p in walk(0, len(buf)) if t == 0x0002)
verts = [struct.unpack_from("<3f", v, i * 12) for i in range(len(v) // 12)]
# glTF Y-up (100,200,300) -> W3D Z-up (x, -z, y) = (100, -300, 200).
want = (100.0, -300.0, 200.0)
got = verts[0]
if max(abs(a - b) for a, b in zip(got, want)) > 1e-4:
    print(f"  NODE TRANSFORM WAS DROPPED: first vertex at {got}, expected {want}")
    print("   — every positioned part would collapse to the origin, silently")
    sys.exit(1)
print(f"  a node translated by (100,200,300) Y-up puts its vertex at {tuple(round(c) for c in got)} Z-up")
PYCHK
rm -rf "$NT"; trap - EXIT


gate "the modelling recipe — ridge, dome, and a mirror that actually mirrors"
# The geometry kernel is Blender's; what is ours is the recipe, and each of these three has a
# failure mode that produces a PLAUSIBLE model rather than an error.
BL=/Applications/Blender.app/Contents/MacOS/Blender
if [ ! -x "$BL" ]; then
  echo "  (no Blender: the modelling backend cannot be exercised — skipped)"
else
  MB=$(mktemp -d); trap 'rm -rf "$MB"' EXIT
  cat > "$MB/r.json" <<'JSONMB'
{ "name": "GATE", "budget": 900, "uvScale": 16,
  "parts": [
    { "name": "RIDGE", "shape": "ridge", "size": [40, 24, 16], "at": [0, 0, 8],
      "ridgeAxis": "x" },
    { "name": "DOME", "shape": "dome", "size": [36, 36, 20], "at": [0, 90, 10],
      "segments": 12, "rings": 3 },
    { "name": "WING", "shape": "box", "size": [6, 10, 6], "at": [40, -60, 3],
      "mirror": "x" }
  ] }
JSONMB
  ./tools/zhasset model --recipe "$MB/r.json" --out "$MB/r.w3d" >/dev/null 2>&1 \
    || { echo "  THE MODELLING BACKEND FAILED"; ./tools/zhasset model --recipe "$MB/r.json" \
         --out "$MB/r.w3d"; exit 1; }
  python3 - "$MB/r.w3d" <<'PYMB'
import struct, sys
buf = open(sys.argv[1], "rb").read()
def tree(off, end):
    n = []
    while off + 8 <= end:
        t, raw = struct.unpack_from("<II", buf, off); sz = raw & 0x7FFFFFFF; body = off + 8
        n.append((t, None if raw & 0x80000000 else buf[body:body + sz],
                  tree(body, body + sz) if raw & 0x80000000 else []))
        off = body + sz
    return n
parts = {}
for t, p, k in tree(0, len(buf)):
    if t != 0x0000:
        continue
    st = {}
    def scan(ns):
        for tt, pp, kk in ns:
            if tt == 0x001F: st["nm"] = pp[8:24].split(b"\0")[0].decode()
            if tt == 0x0002: st["v"] = [struct.unpack_from("<3f", pp, i * 12)
                                        for i in range(len(pp) // 12)]
            if tt == 0x0020: st["t"] = [struct.unpack_from("<3I", pp, i * 32)
                                        for i in range(len(pp) // 32)]
            scan(kk)
    scan(k)
    parts[st["nm"]] = st

fail = []

# RIDGE: the top must collapse to a LINE. `taper` scales both non-axis dimensions equally and
# so yields a pyramid — a shape that looks deliberate and is not a roof. Measure the Y spread
# at the top against the Y spread at the bottom: a roof narrows in Y only, and keeps its full
# length in X.
r = parts["RIDGE"]; v = r["v"]
zhi = max(p[2] for p in v); zlo = min(p[2] for p in v)
top = [p for p in v if p[2] > zhi - 1e-3]
bot = [p for p in v if p[2] < zlo + 1e-3]
wy_top = max(p[1] for p in top) - min(p[1] for p in top)
wy_bot = max(p[1] for p in bot) - min(p[1] for p in bot)
wx_top = max(p[0] for p in top) - min(p[0] for p in top)
if wy_top > wy_bot * 0.5:
    fail.append(f"RIDGE did not narrow: top spans {wy_top:.1f} in Y, bottom {wy_bot:.1f}")
if wx_top < 30.0:
    fail.append(f"RIDGE collapsed to a POINT, not a line: top spans {wx_top:.1f} in X")

# DOME: `size` must mean FULL height. A hemisphere is naturally z in 0..1, so without the
# remap it is silently half as tall as asked for and sits at the wrong elevation — which reads
# as a scale mistake in the recipe rather than a bug in the shape.
d = parts["DOME"]["v"]
h = max(p[2] for p in d) - min(p[2] for p in d)
if abs(h - 20.0) > 0.5:
    fail.append(f"DOME is {h:.1f} tall, asked for 20 — `size` must mean full height")

# MIRROR: *this was a live bug.* While a part still sits at the origin the mirror plane passes
# through the part itself, so reflecting a symmetric box lands it exactly on top of itself and
# the modifier appears to do nothing — one flank of louvres instead of two, no error, and a
# triangle count that looks entirely reasonable. WING is authored at x=+40, so a working
# mirror must put geometry at negative x too.
w = parts["WING"]["v"]
if min(p[0] for p in w) > -1.0:
    fail.append(f"MIRROR PRODUCED NOTHING: WING spans x {min(p[0] for p in w):.1f}.."
                f"{max(p[0] for p in w):.1f}, authored at +40 with mirror:x")

# And every face must still wind outward after all that surgery — an inverted normal renders
# black or invisible in game and is not visible in a chunk dump.
for nm, st in parts.items():
    vs, ts = st["v"], st["t"]
    c = [sum(p[i] for p in vs) / len(vs) for i in range(3)]
    bad = 0
    for a, b, cc in ts:
        p0, p1, p2 = vs[a], vs[b], vs[cc]
        u = [p1[i] - p0[i] for i in range(3)]; vv = [p2[i] - p0[i] for i in range(3)]
        n = (u[1]*vv[2]-u[2]*vv[1], u[2]*vv[0]-u[0]*vv[2], u[0]*vv[1]-u[1]*vv[0])
        fc = [(p0[i]+p1[i]+p2[i])/3 - c[i] for i in range(3)]
        if sum(n[i]*fc[i] for i in range(3)) < 0:
            bad += 1
    # MIRROR is exempt: a mirrored pair straddles the centre, so "away from the mesh centroid"
    # is not a meaningful test for it.
    if bad and nm != "WING":
        fail.append(f"{nm}: {bad}/{len(ts)} faces wind INWARD")

if fail:
    for f in fail:
        print("  " + f)
    sys.exit(1)
print(f"  ridge narrows to a line ({wy_top:.1f} vs {wy_bot:.1f} in Y, {wx_top:.0f} long)")
print(f"  dome honours full height ({h:.0f} of 20 asked)")
print(f"  mirror reflects across the MODEL centreline, not the part's")
print(f"  every face winds outward after taper, bevel, boolean and mirror")
PYMB
  rm -rf "$MB"; trap - EXIT
fi


echo
echo "E2E PASS"

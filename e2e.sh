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

echo
echo "== gate 1/19: content lint =="
rts lint

echo
echo "== gate 2/19: replay determinism =="
rts replay --a crusader --b battlemaster --seed 7

echo
echo "== gate 3/19: duel smoke =="
rts duel --a crusader --b technical --n 20 --seed 42

echo
echo "== gate 4/19: econ determinism =="
rts econ --a "technical*" --b "war_factory,crusader*" --n 20 --seed 42

echo
echo "== gate 5/19: faction layering resolves =="
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

echo
echo "== gate 6/19: layered packs + diff =="
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

echo
echo "== gate 7/19: structures are economic targets =="
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

echo
echo "== gate 8/19: flags and conditional variants =="
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

echo
echo "== gate 9/19: event rules {on, when, do} =="
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

echo
echo "== gate 10/19: factions are startable, not just rosters =="
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

echo
echo "== gate 11/19: compile to a Zero Hour mod =="
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

echo
echo "== gate 12/19: target-zh lint — caps, round-trip, divergence =="
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

echo
echo "== gate 13/19: faction-scoped reference resolution =="
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

echo
echo "== gate 14/19: garrison + capture =="
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

echo
echo "== gate 15/19: sciences — a second currency earned by fighting =="
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

echo
echo "== gate 16/19: spatial index is a PURE accelerator =="
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

echo
echo "== gate 17/19: overrides compile to map.ini, and only there =="
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

echo
echo "== gate 18/19: an emitted object is PLAYABLE, not merely parseable =="
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

# Geometry must match the BORROWED MODEL, not an invented number. ABWarFact is built for
# 53x60; a 22-radius box put every produced unit inside the visible building.
tr -d '\r' < "$OBJ" | awk '/^Object demo_hellfire_works$/,/^End$/' | grep -q "GeometryMajorRadius = 53.0" \
  || { echo "  structure geometry no longer matches the borrowed mesh — units spawn inside it"; exit 1; }
tr -d '\r' < "$OBJ" | awk '/^Object demo_hellfire_works$/,/^End$/' | grep -q "NaturalRallyPoint = X:53.0" \
  || { echo "  NaturalRallyPoint must match GeometryMajorRadius (retail says so in a comment)"; exit 1; }
echo "  geometry, create point and rally point all agree with the adopted mesh"

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
  tr -d '\r' < "$D" | awk '/^Object demo_hellfire_works$/,/^End$/' | grep -q "GeometryMajorRadius = 53.0" \
    || { echo "  structure geometry not derived from the mesh — units spawn inside it"; exit 1; }
  echo "  turret, bones, flash and geometry all derived from the mesh, none hand-authored"
else
  echo "  (skipped: reference/art-profiles.json absent — run tools/zhasset artprofile)"
fi

echo
echo "== gate 19/19: MCP server — the agent-facing seam =="
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

local actor, failure = authenticate(false)
if failure then return result(failure) end
local player = readPlayer(KEYS[6], playerId)
if not player then return result('player_not_found') end
if actor.playerId == playerId then player.lastActive = now end
local body = cjson.encode(player)
touch(actor)
return result('ok', body)

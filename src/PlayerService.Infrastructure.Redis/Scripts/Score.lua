local actor, failure = authenticate(true)
if failure then return result(failure) end
if not points or points <= 0 or points > maximum or points ~= math.floor(points) or #requestId ~= 36 then
    return result('invalid_input')
end
checkType(board, 'zset')
local key = KEYS[6]
local fingerprint = tostring(points)
local replay = cached(key, fingerprint, actor)
if replay then return replay end
local player = actor.player
if player.score > maximum - points then return save(key, fingerprint, 'score_limit') end
player.score = player.score + points
player.lastActive = now
local body = cjson.encode(player)
writeScore(playerKey, playerId, player.score)
touch(actor)
return save(key, fingerprint, 'ok', body)

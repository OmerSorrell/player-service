if not validId(playerId) or not validId(otherId) then return result('invalid_input') end
local device, oldSession = deviceKey, KEYS[6]
checkType(device, 'string')
checkType(sessionKey, 'hash')
checkType(board, 'zset')
checkType(playerSessions, 'zset')
local player = readPlayer(playerKey, playerId)
local old = redis.call('GET', device)
if old then
    -- Another login won after key resolution. Reject without accessing its undeclared session.
    if old ~= previousToken then return result('device_active') end
    checkType(oldSession, 'hash')
    if redis.call('PTTL', oldSession) > 0 and redis.call('HGET', oldSession, 'deviceId') == otherId then
        return result('device_active')
    end
end
if redis.call('EXISTS', sessionKey) == 1 then error('Session token collision') end
local body = cjson.encode({sessionToken=token, expiresInSeconds=lease})
if not player then
    redis.call('HSET', playerKey, 'score', 1000, 'giftsSent', 0, 'giftsReceived', 0, 'lastActive', now)
    redis.call('ZADD', board, 1000, playerId)
end
redis.call('HSET', sessionKey, 'playerId', playerId, 'deviceId', otherId)
redis.call('SET', device, token, 'PX', lease * 1000)
touch({playerId=playerId})
return result('ok', body)

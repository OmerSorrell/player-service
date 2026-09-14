local actor, failure = authenticate(true)
if failure then return result(failure) end
if not validId(otherId) or otherId == playerId or not points or points <= 0 or points > maximum or points ~= math.floor(points) or #requestId ~= 36 then
    return result('invalid_input')
end
checkType(board, 'zset')
local recipientKey, active, key = KEYS[6], KEYS[7], KEYS[8]
local fingerprint = otherId .. ':' .. tostring(points)
local replay = cached(key, fingerprint, actor)
if replay then return replay end
local recipient = readPlayer(recipientKey, otherId)
checkType(active, 'zset')
if not recipient or redis.call('ZCOUNT', active, '(' .. now, '+inf') == 0 then
    return save(key, fingerprint, 'recipient_offline')
end
local sender = actor.player
if sender.score < points then return save(key, fingerprint, 'insufficient_balance') end
if recipient.score > maximum - points or sender.giftsSent == maximum or recipient.giftsReceived == maximum then
    return save(key, fingerprint, 'score_limit')
end
sender.score = sender.score - points
sender.giftsSent = sender.giftsSent + 1
sender.lastActive = now
recipient.score = recipient.score + points
recipient.giftsReceived = recipient.giftsReceived + 1
local body = cjson.encode({requestId=requestId, sender=sender, recipient=recipient})
writeScore(playerKey, playerId, sender.score)
writeScore(recipientKey, otherId, recipient.score)
redis.call('HSET', playerKey, 'giftsSent', sender.giftsSent)
redis.call('HSET', recipientKey, 'giftsReceived', recipient.giftsReceived)
touch(actor)
return save(key, fingerprint, 'ok', body)

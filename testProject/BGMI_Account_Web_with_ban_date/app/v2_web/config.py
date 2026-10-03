import os
from dotenv import load_dotenv

load_dotenv()

BOT_TOKEN = os.getenv('BOT_TOKEN_BGMI')
ADMIN_IDS = [int(id.strip()) for id in os.getenv('ADMIN_IDS', '').split(',') if id.strip()]
PLATFORMS = ['gmail', 'twitter', 'facebook']
STATUS_OPTIONS = ['active', 'banned', 'suspended', 'warning', 'na']
RANK_OPTIONS = ['bronze', 'silver', 'gold', 'platinum', 'diamond', 'crown', 'ace', 'conqueror']
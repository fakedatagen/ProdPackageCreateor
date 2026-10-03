from flask_sqlalchemy import SQLAlchemy
from datetime import datetime
from flask_login import UserMixin

db = SQLAlchemy()

class User(UserMixin, db.Model):
    __tablename__ = 'users'
    id = db.Column(db.Integer, primary_key=True)
    username = db.Column(db.String(80), unique=True, nullable=False)
    password = db.Column(db.String(200), nullable=False)
    is_admin = db.Column(db.Boolean, default=False)
    created_at = db.Column(db.DateTime, default=datetime.utcnow)

class Account(db.Model):
    __tablename__ = 'accounts'
    id = db.Column(db.Integer, primary_key=True)
    email = db.Column(db.String(200), nullable=False)
    platform = db.Column(db.String(50), nullable=False)
    account_name = db.Column(db.String(200), default='')
    status = db.Column(db.String(50), default='na')
    platform_id = db.Column(db.String(100), default='')
    username = db.Column(db.String(100), default='')
    rank = db.Column(db.String(50), default='')
    merit = db.Column(db.Integer, default=0)  # ← ADD THIS LINE
    ban_date = db.Column(db.String(50), default='')
    comment = db.Column(db.Text, default='')
    created_at = db.Column(db.DateTime, default=datetime.utcnow)
    updated_at = db.Column(db.DateTime, default=datetime.utcnow, onupdate=datetime.utcnow)
    
    # Unique constraint
    __table_args__ = (db.UniqueConstraint('email', 'platform', name='unique_email_platform'),)

class BanHistory(db.Model):
    __tablename__ = 'ban_history'
    id = db.Column(db.Integer, primary_key=True)
    account_id = db.Column(db.Integer, db.ForeignKey('accounts.id'))
    banned_at = db.Column(db.DateTime, default=datetime.utcnow)
    reason = db.Column(db.Text, default='')
    
    account = db.relationship('Account', backref=db.backref('ban_history', lazy=True))

class ActivityLog(db.Model):
    __tablename__ = 'activity_logs'
    id = db.Column(db.Integer, primary_key=True)
    user_id = db.Column(db.Integer, db.ForeignKey('users.id'))
    action = db.Column(db.String(200))
    details = db.Column(db.Text)
    timestamp = db.Column(db.DateTime, default=datetime.utcnow)
    
    user = db.relationship('User', backref=db.backref('logs', lazy=True))

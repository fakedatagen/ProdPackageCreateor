from flask import Flask, render_template, request, redirect, url_for, flash, jsonify, send_file
from flask_sqlalchemy import SQLAlchemy
from flask_login import LoginManager, UserMixin, login_user, logout_user, login_required, current_user
from werkzeug.security import generate_password_hash, check_password_hash
from datetime import datetime, timedelta
from sqlalchemy import inspect, text
import os
import io
import re


app = Flask(__name__)
app.config['SECRET_KEY'] = 'your-secret-key-change-this-in-production'
app.config['SQLALCHEMY_DATABASE_URI'] = 'sqlite:///accounts.db'
app.config['SQLALCHEMY_TRACK_MODIFICATIONS'] = False

db = SQLAlchemy(app)
login_manager = LoginManager()
login_manager.init_app(app)
login_manager.login_view = 'login'
login_manager.login_message = 'Please login to access this page'

# ============ MODELS ============
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
    merit = db.Column(db.Integer, default=0)
    ban_date = db.Column(db.String(50), default='')
    comment = db.Column(db.Text, default='')
    created_at = db.Column(db.DateTime, default=datetime.utcnow)
    updated_at = db.Column(db.DateTime, default=datetime.utcnow, onupdate=datetime.utcnow)

class BanHistory(db.Model):
    __tablename__ = 'ban_history'
    id = db.Column(db.Integer, primary_key=True)
    account_id = db.Column(db.Integer, db.ForeignKey('accounts.id'))
    banned_at = db.Column(db.DateTime, default=datetime.utcnow)
    reason = db.Column(db.Text, default='')
    
    account = db.relationship('Account', backref=db.backref('ban_history', lazy=True))

# ============ USER LOADER ============
@login_manager.user_loader
def load_user(user_id):
    return db.session.get(User, int(user_id))

# ============ CREATE TABLES AND DEFAULT ADMIN ============
def ensure_account_schema():
    columns = {column['name'] for column in inspect(db.engine).get_columns('accounts')}
    if 'ban_date' not in columns:
        db.session.execute(text("ALTER TABLE accounts ADD COLUMN ban_date VARCHAR(50) DEFAULT ''"))
        db.session.commit()

    for account in Account.query.filter(Account.comment.contains('Unban:')).all():
        if account.ban_date:
            continue
        match = re.search(r'Unban:\s*([^|]+)', account.comment or '')
        if match:
            account.ban_date = match.group(1).strip()
    db.session.commit()

with app.app_context():
    db.create_all()
    ensure_account_schema()
    
    if not User.query.filter_by(username='admin').first():
        admin = User(
            username='admin',
            password=generate_password_hash('admin123'),
            is_admin=True
        )
        db.session.add(admin)
        db.session.commit()
        print("="*50)
        print("✅ Default admin created!")
        print("📝 Username: admin")
        print("🔑 Password: admin123")
        print("="*50)

# ============ AUTH ROUTES ============
@app.route('/')
def index():
    if current_user.is_authenticated:
        return redirect(url_for('dashboard'))
    return redirect(url_for('login'))

@app.route('/login', methods=['GET', 'POST'])
def login():
    if current_user.is_authenticated:
        return redirect(url_for('dashboard'))
    
    if request.method == 'POST':
        username = request.form.get('username')
        password = request.form.get('password')
        user = User.query.filter_by(username=username).first()
        
        if user and check_password_hash(user.password, password):
            login_user(user)
            flash(f'Welcome back, {username}!', 'success')
            return redirect(url_for('dashboard'))
        else:
            flash('Invalid username or password', 'danger')
    
    return render_template('login.html')

@app.route('/logout')
@login_required
def logout():
    logout_user()
    flash('You have been logged out', 'info')
    return redirect(url_for('login'))

# ============ MAIN ROUTES ============
@app.route('/dashboard')
@login_required
def dashboard():
    total_accounts = Account.query.count()
    active_accounts = Account.query.filter_by(status='active').count()
    banned_accounts = Account.query.filter_by(status='banned').count()
    na_accounts = Account.query.filter_by(status='na').count()
    
    recent_accounts = Account.query.order_by(Account.updated_at.desc()).limit(10).all()
    
    platforms = db.session.query(Account.platform, db.func.count()).group_by(Account.platform).all()
    platform_data = {p[0]: p[1] for p in platforms}
    
    return render_template('index.html', 
                         total=total_accounts,
                         active=active_accounts,
                         banned=banned_accounts,
                         na=na_accounts,
                         recent_accounts=recent_accounts,
                         platform_data=platform_data)

@app.route('/active-accounts')
@login_required
def active_accounts():
    """Show only active accounts"""
    page = request.args.get('page', 1, type=int)
    search = request.args.get('search', '')
    platform = request.args.get('platform', '')
    
    query = Account.query.filter_by(status='active')
    
    if search:
        query = query.filter(
            db.or_(
                Account.email.contains(search),
                Account.account_name.contains(search),
                Account.username.contains(search)
            )
        )
    if platform:
        query = query.filter_by(platform=platform)
    
    pagination = query.order_by(Account.updated_at.desc()).paginate(page=page, per_page=20, error_out=False)
    accounts_list = pagination.items
    
    return render_template('active_accounts.html', 
                         accounts=accounts_list, 
                         pagination=pagination,
                         search=search,
                         platform=platform)

@app.route('/na-accounts')
@login_required
def na_accounts():
    """Show only NA (Not in use) accounts"""
    page = request.args.get('page', 1, type=int)
    search = request.args.get('search', '')
    platform = request.args.get('platform', '')
    
    query = Account.query.filter_by(status='na')
    
    if search:
        query = query.filter(
            db.or_(
                Account.email.contains(search),
                Account.account_name.contains(search),
                Account.username.contains(search)
            )
        )
    if platform:
        query = query.filter_by(platform=platform)
    
    pagination = query.order_by(Account.email).paginate(page=page, per_page=20, error_out=False)
    accounts_list = pagination.items
    
    return render_template('na_accounts.html', 
                         accounts=accounts_list, 
                         pagination=pagination,
                         search=search,
                         platform=platform)

@app.route('/accounts')
@login_required
def accounts():
    page = request.args.get('page', 1, type=int)
    platform = request.args.get('platform', '')
    status = request.args.get('status', '')
    search = request.args.get('search', '')
    
    query = Account.query
    
    if platform:
        query = query.filter_by(platform=platform)
    if status:
        query = query.filter_by(status=status)
    if search:
        query = query.filter(
            db.or_(
                Account.email.contains(search),
                Account.account_name.contains(search),
                Account.username.contains(search)
            )
        )
    
    pagination = query.order_by(Account.updated_at.desc()).paginate(page=page, per_page=20, error_out=False)
    accounts_list = pagination.items
    
    return render_template('accounts.html', 
                         accounts=accounts_list, 
                         pagination=pagination,
                         platform=platform,
                         status=status,
                         search=search)

@app.route('/account/add', methods=['GET', 'POST'])
@login_required
def add_account():
    if request.method == 'POST':
        try:
            # Get merit value
            merit_value = request.form.get('merit', 0)
            try:
                merit_value = int(merit_value)
                if merit_value < 0: merit_value = 0
                if merit_value > 999: merit_value = 999
            except:
                merit_value = 0
            
            account = Account(
                email=request.form.get('email'),
                platform=request.form.get('platform'),
                account_name=request.form.get('account_name', ''),
                status=request.form.get('status', 'na'),
                platform_id=request.form.get('platform_id', ''),
                username=request.form.get('username', ''),
                rank=request.form.get('rank', ''),
                merit=merit_value,  # ← ADD THIS
                ban_date=request.form.get('ban_date', '').strip(),
                comment=request.form.get('comment', '')
            )
            db.session.add(account)
            db.session.commit()
            flash('Account added successfully!', 'success')
            return redirect(url_for('accounts'))
        except Exception as e:
            db.session.rollback()
            flash(f'Error adding account: {str(e)}', 'danger')
    
    return render_template('add_account.html')

@app.route('/account/<int:account_id>')
@login_required
def view_account(account_id):
    account = Account.query.get_or_404(account_id)
    return render_template('view_account.html', account=account)

@app.route('/account/<int:account_id>/edit', methods=['GET', 'POST'])
@login_required
def edit_account(account_id):
    account = Account.query.get_or_404(account_id)
    
    if request.method == 'POST':
        # Get merit value
        merit_value = request.form.get('merit', 0)
        try:
            merit_value = int(merit_value)
            if merit_value < 0: merit_value = 0
            if merit_value > 999: merit_value = 999
        except:
            merit_value = 0
        
        account.email = request.form.get('email')
        account.platform = request.form.get('platform')
        account.account_name = request.form.get('account_name', '')
        account.status = request.form.get('status', 'na')
        account.platform_id = request.form.get('platform_id', '')
        account.username = request.form.get('username', '')
        account.rank = request.form.get('rank', '')
        account.merit = merit_value  # ← ADD THIS
        account.ban_date = request.form.get('ban_date', '').strip()
        account.comment = request.form.get('comment', '')
        account.updated_at = datetime.utcnow()
        
        db.session.commit()
        flash('Account updated successfully!', 'success')
        return redirect(url_for('view_account', account_id=account_id))
    
    return render_template('edit_account.html', account=account)

@app.route('/account/<int:account_id>/ban', methods=['POST'])
@login_required
def ban_account(account_id):
    account = Account.query.get_or_404(account_id)
    reason = request.form.get('reason', '')
    ban_date = request.form.get('ban_date', reason).strip()
    
    account.status = 'banned'
    account.ban_date = ban_date
    # Keep older comment-based displays/imports understandable.
    if ban_date and (ban_date.isdigit() or '-' in ban_date):
        if account.comment:
            account.comment = f"Unban: {ban_date} | {account.comment}"
        else:
            account.comment = f"Unban: {ban_date}"
    
    ban_record = BanHistory(account_id=account_id, reason=reason)
    db.session.add(ban_record)
    db.session.commit()
    
    flash(f'Account {account.email} has been banned!', 'warning')
    return redirect(url_for('view_account', account_id=account_id))

@app.route('/account/<int:account_id>/activate', methods=['POST'])
@login_required
def activate_account(account_id):
    account = Account.query.get_or_404(account_id)
    account.status = 'active'
    account.ban_date = ''
    db.session.commit()
    
    flash(f'Account {account.email} has been activated!', 'success')
    return redirect(url_for('view_account', account_id=account_id))

@app.route('/account/<int:account_id>/delete', methods=['POST'])
@login_required
def delete_account(account_id):
    account = Account.query.get_or_404(account_id)
    email = account.email
    
    db.session.delete(account)
    db.session.commit()
    
    flash(f'Account {email} has been deleted!', 'danger')
    return redirect(url_for('accounts'))

@app.route('/banned')
@login_required
def banned_accounts():
    banned_list = Account.query.filter_by(status='banned').order_by(Account.updated_at.desc()).all()
    return render_template('banned.html', accounts=banned_list)

@app.route('/stats')
@login_required
def stats():
    total = Account.query.count()
    
    status_stats = db.session.query(Account.status, db.func.count()).group_by(Account.status).all()
    platform_stats = db.session.query(Account.platform, db.func.count()).group_by(Account.platform).all()
    rank_stats = db.session.query(Account.rank, db.func.count()).filter(Account.rank != '').group_by(Account.rank).all()
    
    return render_template('stats.html',
                         total=total,
                         status_stats=status_stats,
                         platform_stats=platform_stats,
                         rank_stats=rank_stats)

# ============ IMPORT/EXPORT ROUTES ============
@app.route('/import', methods=['GET', 'POST'])
@login_required
def import_excel():
    if request.method == 'POST':
        if 'file' not in request.files:
            flash('No file uploaded', 'danger')
            return redirect(request.url)
        
        file = request.files['file']
        if file.filename == '':
            flash('No file selected', 'danger')
            return redirect(request.url)
        
        if not file.filename.endswith(('.xlsx', '.xls')):
            flash('Please upload an Excel file (.xlsx or .xls)', 'danger')
            return redirect(request.url)
        
        try:
            import pandas as pd
            import re
            
            # Read all sheets
            excel_file = pd.ExcelFile(file)
            total_imported = 0
            total_updated = 0
            
            def clean_id(id_value):
                """Convert ID to integer string without .0"""
                if pd.isna(id_value):
                    return ''
                # Convert to string and remove .0 if present
                id_str = str(id_value).strip()
                if id_str.endswith('.0'):
                    id_str = id_str[:-2]
                return id_str
            
            def parse_status(status_value):
                if pd.isna(status_value):
                    return 'na', ''
                
                status_str = str(status_value).strip()
                
                if status_str.lower() == 'active':
                    return 'active', ''
                
                if status_str.upper() == 'NA' or status_str == '' or status_str == 'nan':
                    return 'na', ''
                
                if re.match(r'\d{4}$', status_str):
                    return 'banned', status_str
                elif re.match(r'\d{4}-\d{2}-\d{2}', status_str):
                    return 'banned', status_str
                elif re.match(r'\d{1,2}\s+\w+\s+\d{4}', status_str):
                    return 'banned', status_str
                else:
                    if status_str.isdigit():
                        return 'banned', status_str
                    return 'na', ''
            
            # Process Gmail sheet
            if 'Gmail' in excel_file.sheet_names:
                df_gmail = pd.read_excel(file, sheet_name='Gmail')
                for _, row in df_gmail.iterrows():
                    if pd.notna(row.get('Email')):
                        email = row['Email']
                        existing = Account.query.filter_by(email=email, platform='gmail').first()
                        
                        status_value = row.get('Status', 'NA')
                        status, unban_date = parse_status(status_value)
                        if not unban_date and pd.notna(row.get('Ban Date')):
                            unban_date = str(row.get('Ban Date')).strip()
                        
                        # Clean the ID - remove .0
                        platform_id = clean_id(row.get('ID', ''))
                        
                        comment = str(row.get('Comment', '')) if pd.notna(row.get('Comment')) else ''
                        if unban_date:
                            if comment:
                                comment = f"Unban: {unban_date} | {comment}"
                            else:
                                comment = f"Unban: {unban_date}"
                        
                        account_data = {
                            'email': email,
                            'platform': 'gmail',
                            'account_name': str(row.get('Account Name', '')) if pd.notna(row.get('Account Name')) else '',
                            'status': status,
                            'platform_id': platform_id,
                            'username': str(row.get('Username', '')) if pd.notna(row.get('Username')) else '',
                            'rank': str(row.get('Rank', '')).lower() if pd.notna(row.get('Rank')) else '',
                            'ban_date': unban_date,
                            'comment': comment
                        }
                        
                        if existing:
                            for key, value in account_data.items():
                                if key != 'email' and key != 'platform':
                                    setattr(existing, key, value)
                            existing.updated_at = datetime.utcnow()
                            total_updated += 1
                        else:
                            new_account = Account(**account_data)
                            db.session.add(new_account)
                            total_imported += 1
            
            # Process Twitter sheet
            if 'Twitter' in excel_file.sheet_names:
                df_twitter = pd.read_excel(file, sheet_name='Twitter')
                for _, row in df_twitter.iterrows():
                    if pd.notna(row.get('Email')):
                        email = row['Email']
                        existing = Account.query.filter_by(email=email, platform='twitter').first()
                        
                        status_value = row.get('Status', 'NA')
                        status, unban_date = parse_status(status_value)
                        if not unban_date and pd.notna(row.get('Ban Date')):
                            unban_date = str(row.get('Ban Date')).strip()
                        
                        # Clean the ID - remove .0
                        platform_id = clean_id(row.get('ID', ''))
                        
                        comment = str(row.get('Comment', '')) if pd.notna(row.get('Comment')) else ''
                        if unban_date:
                            if comment:
                                comment = f"Unban: {unban_date} | {comment}"
                            else:
                                comment = f"Unban: {unban_date}"
                        
                        account_name = str(row.get('Account Name', '')) if pd.notna(row.get('Account Name')) else ''
                        if '<br>' in account_name:
                            account_name = account_name.replace('<br>', '')
                        
                        account_data = {
                            'email': email,
                            'platform': 'twitter',
                            'account_name': account_name,
                            'status': status,
                            'platform_id': platform_id,
                            'username': str(row.get('Username', '')) if pd.notna(row.get('Username')) else '',
                            'rank': str(row.get('Rank', '')).lower() if pd.notna(row.get('Rank')) else '',
                            'ban_date': unban_date,
                            'comment': comment
                        }
                        
                        if existing:
                            for key, value in account_data.items():
                                if key != 'email' and key != 'platform':
                                    setattr(existing, key, value)
                            existing.updated_at = datetime.utcnow()
                            total_updated += 1
                        else:
                            new_account = Account(**account_data)
                            db.session.add(new_account)
                            total_imported += 1
            
            # Process Facebook sheet
            if 'Facebook' in excel_file.sheet_names:
                df_facebook = pd.read_excel(file, sheet_name='Facebook')
                for _, row in df_facebook.iterrows():
                    if pd.notna(row.get('Email')):
                        email = row['Email']
                        existing = Account.query.filter_by(email=email, platform='facebook').first()
                        
                        status_value = row.get('Status', 'NA')
                        status, unban_date = parse_status(status_value)
                        if not unban_date and pd.notna(row.get('Ban Date')):
                            unban_date = str(row.get('Ban Date')).strip()
                        
                        # Clean the ID - remove .0
                        platform_id = clean_id(row.get('ID', ''))
                        
                        comment = str(row.get('Comment', '')) if pd.notna(row.get('Comment')) else ''
                        if unban_date:
                            if comment:
                                comment = f"Unban: {unban_date} | {comment}"
                            else:
                                comment = f"Unban: {unban_date}"
                        
                        account_data = {
                            'email': email,
                            'platform': 'facebook',
                            'account_name': str(row.get('Account Name', '')) if pd.notna(row.get('Account Name')) else '',
                            'status': status,
                            'platform_id': platform_id,
                            'username': str(row.get('Username', '')) if pd.notna(row.get('Username')) else '',
                            'rank': str(row.get('Rank', '')).lower() if pd.notna(row.get('Rank')) else '',
                            'ban_date': unban_date,
                            'comment': comment
                        }
                        
                        if existing:
                            for key, value in account_data.items():
                                if key != 'email' and key != 'platform':
                                    setattr(existing, key, value)
                            existing.updated_at = datetime.utcnow()
                            total_updated += 1
                        else:
                            new_account = Account(**account_data)
                            db.session.add(new_account)
                            total_imported += 1
            
            db.session.commit()
            flash(f'Successfully imported {total_imported} new accounts and updated {total_updated} existing accounts!', 'success')
            return redirect(url_for('accounts'))
            
        except Exception as e:
            db.session.rollback()
            flash(f'Error importing file: {str(e)}', 'danger')
            return redirect(request.url)
    
    return render_template('import.html')

@app.route('/export')
@login_required
def export_data():
    try:
        import pandas as pd
        
        accounts = Account.query.all()
        data = []
        for acc in accounts:
            data.append({
                'ID': acc.id,
                'Email': acc.email,
                'Platform': acc.platform,
                'Account Name': acc.account_name,
                'Status': acc.status,
                'Platform ID': acc.platform_id,
                'Username': acc.username,
                'Rank': acc.rank,
                'Merit': acc.merit or 0,  # ← ADD THIS
                'Ban Date': acc.ban_date or '',
                'Comment': acc.comment,
                'Created At': acc.created_at.strftime('%Y-%m-%d %H:%M:%S') if acc.created_at else '',
                'Updated At': acc.updated_at.strftime('%Y-%m-%d %H:%M:%S') if acc.updated_at else ''
            })
        
        df = pd.DataFrame(data)
        output = io.BytesIO()
        with pd.ExcelWriter(output, engine='openpyxl') as writer:
            df.to_excel(writer, sheet_name='Accounts', index=False)
        
        output.seek(0)
        
        return send_file(
            output,
            mimetype='application/vnd.openxmlformats-officedocument.spreadsheetml.sheet',
            as_attachment=True,
            download_name=f'bgmi_accounts_{datetime.now().strftime("%Y%m%d_%H%M%S")}.xlsx'
        )
    except Exception as e:
        flash(f'Export error: {str(e)}', 'danger')
        return redirect(url_for('dashboard'))

# ============ RUN APP ============
if __name__ == '__main__':
    print("\n" + "="*50)
    print("🚀 BGMI Account Manager Starting...")
    print("="*50)
    print("📍 Access the web app at: http://localhost:5000")
    print("🔑 Login credentials:")
    print("   Username: admin")
    print("   Password: admin123")
    print("="*50 + "\n")
    app.run(debug=True, host='0.0.0.0', port=5000)

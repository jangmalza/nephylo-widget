using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Markup;
using System.Windows.Media;

namespace UsageWidget
{
    public partial class WidgetWindow
    {
        // Explicit keys keep popup styling independent of the Windows theme and other controls.
        private static ResourceDictionary MakeChromeResources()
        {
            return WidgetPalette.ParseResources(@"
            <ResourceDictionary xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'
                                xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'>
                <Style x:Key='WidgetServiceToggleFocus' TargetType='Control'>
                    <Setter Property='Template'>
                        <Setter.Value>
                            <ControlTemplate TargetType='Control'>
                                <Border BorderBrush='#647070' BorderThickness='1' CornerRadius='3'/>
                            </ControlTemplate>
                        </Setter.Value>
                    </Setter>
                </Style>
                <Style x:Key='WidgetServiceToggle' TargetType='Button'>
                    <Setter Property='Cursor' Value='Hand'/>
                    <Setter Property='Foreground' Value='#647070'/>
                    <Setter Property='Background' Value='Transparent'/>
                    <Setter Property='BorderThickness' Value='1'/>
                    <Setter Property='Padding' Value='0,2'/>
                    <Setter Property='Margin' Value='-1,0,-1,0'/>
                    <Setter Property='HorizontalContentAlignment' Value='Stretch'/>
                    <Setter Property='VerticalContentAlignment' Value='Center'/>
                    <Setter Property='FocusVisualStyle' Value='{StaticResource WidgetServiceToggleFocus}'/>
                    <Setter Property='Template'>
                        <Setter.Value>
                            <ControlTemplate TargetType='Button'>
                                <Border x:Name='Surface' Background='{TemplateBinding Background}'
                                        CornerRadius='3' BorderThickness='{TemplateBinding BorderThickness}'
                                        BorderBrush='Transparent' Padding='{TemplateBinding Padding}'>
                                    <ContentPresenter HorizontalAlignment='{TemplateBinding HorizontalContentAlignment}'
                                                      VerticalAlignment='{TemplateBinding VerticalContentAlignment}'/>
                                </Border>
                                <ControlTemplate.Triggers>
                                    <Trigger Property='IsMouseOver' Value='True'>
                                        <Setter TargetName='Surface' Property='Background' Value='#F2F3EF'/>
                                        <Setter Property='Foreground' Value='#2448F5'/>
                                    </Trigger>
                                    <Trigger Property='IsPressed' Value='True'>
                                        <Setter TargetName='Surface' Property='Background' Value='#E9EDFF'/>
                                    </Trigger>
                                    <Trigger Property='IsEnabled' Value='False'>
                                        <Setter TargetName='Surface' Property='Opacity' Value='0.4'/>
                                    </Trigger>
                                </ControlTemplate.Triggers>
                            </ControlTemplate>
                        </Setter.Value>
                    </Setter>
                </Style>
                <Style x:Key='WidgetToolbarButton' TargetType='Button'>
                    <Setter Property='Width' Value='28'/>
                    <Setter Property='Height' Value='28'/>
                    <Setter Property='Padding' Value='0'/>
                    <Setter Property='Cursor' Value='Hand'/>
                    <Setter Property='Background' Value='Transparent'/>
                    <Setter Property='Foreground' Value='#647070'/>
                    <Setter Property='BorderThickness' Value='0'/>
                    <Setter Property='Template'>
                        <Setter.Value>
                            <ControlTemplate TargetType='Button'>
                                <Border x:Name='Surface' Background='{TemplateBinding Background}'
                                        CornerRadius='3' BorderThickness='1' BorderBrush='Transparent'>
                                    <ContentPresenter HorizontalAlignment='Center' VerticalAlignment='Center'/>
                                </Border>
                                <ControlTemplate.Triggers>
                                    <Trigger Property='IsMouseOver' Value='True'>
                                        <Setter TargetName='Surface' Property='Background' Value='#E9EDFF'/>
                                        <Setter Property='Foreground' Value='#2448F5'/>
                                    </Trigger>
                                    <Trigger Property='IsKeyboardFocused' Value='True'>
                                        <Setter TargetName='Surface' Property='BorderBrush' Value='#2448F5'/>
                                    </Trigger>
                                    <DataTrigger Binding='{Binding ContextMenu.IsOpen, RelativeSource={RelativeSource Self}}' Value='True'>
                                        <Setter TargetName='Surface' Property='Background' Value='#E9EDFF'/>
                                        <Setter Property='Foreground' Value='#2448F5'/>
                                    </DataTrigger>
                                    <Trigger Property='IsPressed' Value='True'>
                                        <Setter TargetName='Surface' Property='Background' Value='#DCE3FF'/>
                                    </Trigger>
                                    <Trigger Property='IsEnabled' Value='False'>
                                        <Setter TargetName='Surface' Property='Opacity' Value='0.35'/>
                                    </Trigger>
                                </ControlTemplate.Triggers>
                            </ControlTemplate>
                        </Setter.Value>
                    </Setter>
                </Style>
                <Style x:Key='WidgetCompactService' TargetType='Button' BasedOn='{StaticResource WidgetServiceToggle}'>
                    <Setter Property='Template'>
                        <Setter.Value>
                            <ControlTemplate TargetType='Button'>
                                <Grid>
                                    <Border x:Name='Surface' Background='{TemplateBinding Background}' CornerRadius='6'
                                            BorderThickness='{TemplateBinding BorderThickness}' BorderBrush='Transparent' Padding='{TemplateBinding Padding}'>
                                        <ContentPresenter HorizontalAlignment='{TemplateBinding HorizontalContentAlignment}' VerticalAlignment='Center'/>
                                    </Border>
                                    <Border x:Name='Divider' Width='1' Height='16' HorizontalAlignment='Left' VerticalAlignment='Center'
                                            Background='#C8CECF' Opacity='0.6' IsHitTestVisible='False' Visibility='Collapsed'/>
                                </Grid>
                                <ControlTemplate.Triggers>
                                    <Trigger Property='Tag' Value='divider'>
                                        <Setter TargetName='Divider' Property='Visibility' Value='Visible'/>
                                    </Trigger>
                                    <Trigger Property='IsMouseOver' Value='True'>
                                        <Setter TargetName='Surface' Property='Background' Value='#F2F3EF'/>
                                    </Trigger>
                                    <Trigger Property='IsKeyboardFocused' Value='True'>
                                        <Setter TargetName='Surface' Property='BorderBrush' Value='#647070'/>
                                    </Trigger>
                                    <DataTrigger Binding='{Binding ContextMenu.IsOpen, RelativeSource={RelativeSource Self}}' Value='True'>
                                        <Setter TargetName='Surface' Property='Background' Value='#F2F3EF'/>
                                    </DataTrigger>
                                    <Trigger Property='IsPressed' Value='True'>
                                        <Setter TargetName='Surface' Property='Background' Value='#E9EDFF'/>
                                    </Trigger>
                                    <Trigger Property='IsEnabled' Value='False'>
                                        <Setter TargetName='Surface' Property='Opacity' Value='0.4'/>
                                    </Trigger>
                                </ControlTemplate.Triggers>
                            </ControlTemplate>
                        </Setter.Value>
                    </Setter>
                </Style>
                <Style x:Key='WidgetCompactAction' TargetType='Button' BasedOn='{StaticResource WidgetCompactService}'>
                    <Setter Property='Height' Value='28'/>
                    <Setter Property='Padding' Value='0'/>
                    <Setter Property='Margin' Value='0'/>
                    <Setter Property='HorizontalContentAlignment' Value='Center'/>
                    <Setter Property='FocusVisualStyle' Value='{x:Null}'/>
                </Style>
                <Style x:Key='WidgetContextMenu' TargetType='ContextMenu'>
                    <Setter Property='OverridesDefaultStyle' Value='True'/>
                    <Setter Property='Background' Value='#FBFBF8'/>
                    <Setter Property='Foreground' Value='#252827'/>
                    <Setter Property='BorderBrush' Value='#D6D9D7'/>
                    <Setter Property='BorderThickness' Value='1'/>
                    <Setter Property='Padding' Value='5'/>
                    <Setter Property='MinWidth' Value='192'/>
                    <Setter Property='FontSize' Value='12'/>
                    <Setter Property='SnapsToDevicePixels' Value='True'/>
                    <Setter Property='UseLayoutRounding' Value='True'/>
                    <Setter Property='Template'>
                        <Setter.Value>
                            <ControlTemplate TargetType='ContextMenu'>
                                <Border Background='{TemplateBinding Background}'
                                        BorderBrush='{TemplateBinding BorderBrush}'
                                        BorderThickness='{TemplateBinding BorderThickness}'
                                        Padding='{TemplateBinding Padding}' CornerRadius='3'>
                                    <ItemsPresenter KeyboardNavigation.DirectionalNavigation='Cycle'/>
                                </Border>
                            </ControlTemplate>
                        </Setter.Value>
                    </Setter>
                </Style>
                <Style x:Key='WidgetMenuItem' TargetType='MenuItem'>
                    <Setter Property='OverridesDefaultStyle' Value='True'/>
                    <Setter Property='Background' Value='Transparent'/>
                    <Setter Property='Foreground' Value='#252827'/>
                    <Setter Property='Padding' Value='10,8'/>
                    <Setter Property='HorizontalContentAlignment' Value='Stretch'/>
                    <Setter Property='Template'>
                        <Setter.Value>
                            <ControlTemplate TargetType='MenuItem'>
                                <Border x:Name='Surface' Background='{TemplateBinding Background}'
                                        CornerRadius='3' Padding='{TemplateBinding Padding}'>
                                    <Grid>
                                        <Grid.ColumnDefinitions>
                                            <ColumnDefinition Width='22'/>
                                            <ColumnDefinition Width='*'/>
                                            <ColumnDefinition Width='20'/>
                                        </Grid.ColumnDefinitions>
                                        <ContentPresenter ContentSource='Icon' VerticalAlignment='Center' Opacity='0.85'/>
                                        <ContentPresenter Grid.Column='1' ContentSource='Header'
                                                          RecognizesAccessKey='True' VerticalAlignment='Center'/>
                                        <TextBlock x:Name='Check' Grid.Column='2' Text='&#xE73E;'
                                                   FontFamily='Segoe MDL2 Assets' FontSize='11'
                                                   Foreground='#2448F5' HorizontalAlignment='Right'
                                                   VerticalAlignment='Center' Visibility='Collapsed'/>
                                    </Grid>
                                </Border>
                                <ControlTemplate.Triggers>
                                    <Trigger Property='IsHighlighted' Value='True'>
                                        <Setter TargetName='Surface' Property='Background' Value='#E9EDFF'/>
                                        <Setter Property='Foreground' Value='#2448F5'/>
                                    </Trigger>
                                    <Trigger Property='IsChecked' Value='True'>
                                        <Setter TargetName='Check' Property='Visibility' Value='Visible'/>
                                    </Trigger>
                                    <Trigger Property='IsEnabled' Value='False'>
                                        <Setter TargetName='Surface' Property='Opacity' Value='0.4'/>
                                    </Trigger>
                                </ControlTemplate.Triggers>
                            </ControlTemplate>
                        </Setter.Value>
                    </Setter>
                </Style>
                <Style x:Key='WidgetMenuSeparator' TargetType='Separator'>
                    <Setter Property='Template'>
                        <Setter.Value>
                            <ControlTemplate TargetType='Separator'>
                                <Border Height='1' Background='#D6D9D7' Margin='10,4'/>
                            </ControlTemplate>
                        </Setter.Value>
                    </Setter>
                </Style>
            </ResourceDictionary>");
        }

        private MenuItem WidgetMenuItem(string title, string icon)
        {
            return new LocalizedMenuItem {
                Header = title, Style = (Style)Resources["WidgetMenuItem"],
                Icon = new LocalizedTextBlock { Text = icon, FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 12 }
            };
        }

        private void OpenMoreMenu(Button more)
        {
            var menu = CreateMoreMenu(more);
            more.ContextMenu = menu; menu.IsOpen = true;
        }

        private ContextMenu CreateMoreMenu(Button more)
        {
            var menu = new ContextMenu {
                Style = (Style)Resources["WidgetContextMenu"], FontFamily = FontFamily,
                PlacementTarget = more, Placement = PlacementMode.Custom,
                CustomPopupPlacementCallback = (popup, target, offset) => new[] {
                    new CustomPopupPlacement(new Point(target.Width - popup.Width, target.Height + 6), PopupPrimaryAxis.Horizontal),
                    new CustomPopupPlacement(new Point(target.Width - popup.Width, -popup.Height - 6), PopupPrimaryAxis.Horizontal)
                }
            };
            var dashboard = WidgetMenuItem("대시보드 열기", "\uE80F");
            dashboard.Click += (s,e) => OpenDashboard(); menu.Items.Add(dashboard);
            var history = WidgetMenuItem("사용 이력", "\uE9D9");
            history.Click += (s,e) => OpenUsageHistory(); menu.Items.Add(history);
            var weekly = WidgetMenuItem("주간 요약", "\uE9D9");
            weekly.Click += (s,e) => OpenWeeklySummary(); menu.Items.Add(weekly);
            var notifications = WidgetMenuItem("알림 · 이력 설정", "\uE7F4");
            notifications.Click += (s,e) => OpenNotificationSettings(); menu.Items.Add(notifications);
            var plan = WidgetMenuItem("무료 이용 안내", "\uE8D4");
            plan.Click += (s,e) => OpenPlan(); menu.Items.Add(plan);
            var sponsor = WidgetMenuItem("후원하기 ↗", "\uEB51");
            sponsor.Click += (s,e) => OpenSponsorship(); menu.Items.Add(sponsor);
            var release = WidgetMenuItem("버전 · 설치 · 업데이트", "\uE946");
            release.Click += (s,e) => OpenReleaseInfo(); menu.Items.Add(release);
            var onboarding = WidgetMenuItem("연결 시작 도우미", "\uE946");
            onboarding.Click += (s,e) => OpenOnboarding(); menu.Items.Add(onboarding);
            menu.Items.Add(new Separator { Style = (Style)Resources["WidgetMenuSeparator"] });
            var pin = WidgetMenuItem("항상 위에 표시", "\uE718");
            pin.IsCheckable = true; pin.IsChecked = Topmost;
            pin.Click += (s,e) => { Topmost = pin.IsChecked; settings.AlwaysOnTop = Topmost; UpdatePin(); if (!demo) TrySaveSettings(); };
            menu.Items.Add(pin);
            var compact = WidgetMenuItem("간략 보기", "\uE8A5");
            compact.IsCheckable = true; compact.IsChecked = settings.CompactView;
            compact.Click += (s,e) => SetCompactView(compact.IsChecked); menu.Items.Add(compact);
            var compactWidth = WidgetMenuItem("서비스 수에 맞춘 너비", "\uE8A5");
            compactWidth.IsCheckable = true; compactWidth.IsChecked = settings.CompactAutoWidth;
            compactWidth.IsEnabled = settings.CompactView;
            compactWidth.ToolTip = "표시하는 서비스 수에 맞춰 미니바 너비를 조절합니다. 직접 크기를 조절하면 자동 너비가 꺼지며 이 메뉴에서 다시 켤 수 있습니다.";
            compactWidth.Click += (s,e) => SetCompactAutoWidth(compactWidth.IsChecked); menu.Items.Add(compactWidth);
            var fit = WidgetMenuItem("내용에 맞는 높이", "\uE740");
            fit.IsCheckable = true; fit.IsChecked = settings.FitContentHeight;
            fit.IsEnabled = !settings.CompactView;
            fit.Click += (s,e) => SetFitContentHeight(fit.IsChecked); menu.Items.Add(fit);
            var hide = WidgetMenuItem("트레이로 숨기기", "\uE921");
            hide.Click += (s,e) => Hide(); menu.Items.Add(hide);
            menu.Items.Add(new Separator { Style = (Style)Resources["WidgetMenuSeparator"] });
            var quit = WidgetMenuItem("종료", "\uE8BB");
            quit.Click += (s,e) => Close(); menu.Items.Add(quit);
            return menu;
        }

        private void SetCompactAutoWidth(bool enabled)
        {
            bool previous = settings.CompactAutoWidth;
            if (previous == enabled) return;
            settings.CompactAutoWidth = enabled;
            try { if (!demo) SettingsStore.Save(settings); }
            catch {
                settings.CompactAutoWidth = previous;
                if (footer != null) footer.Text = "너비 설정을 저장하지 못했습니다.";
                return;
            }
            if (enabled) {
                ResetCompactWidthMeasurement();
                RefreshCompactBar();
                QueueCompactAutoWidth();
            }
        }
    }
}
